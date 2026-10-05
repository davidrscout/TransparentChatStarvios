using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransparentChatStarvios;

// Starvios guarda el chat en Supabase y lo deja leer de forma pública con su clave
// "publishable" (la misma que lleva su web). Solo LEEMOS: nunca escribimos ni hace falta sesión.
public sealed class StarviosClient : IDisposable
{
    const string Project = "https://naiijcvngtkkocligwvk.supabase.co";
    const string PublicKey = "sb_publishable_4lF1QyQhWOUE1Pw3rWBwEw_bVmkAxm6";
    const string MessageSelect =
        "id,user_id,body,coins,kind,meta,badges,is_deleted,created_at," +
        "profiles:profiles!live_chat_messages_user_id_fkey(username,display_name,is_partner,is_verified,is_team,chat_color)";

    readonly HttpClient _http;

    public StarviosClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        { BaseAddress = new Uri(Project), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("apikey", PublicKey);
        _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + PublicKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TransparentChatStarvios/1.0");
    }

    async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(s, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<StreamInfo?> ResolveAsync(string username, CancellationToken ct)
    {
        var profiles = await GetAsync<List<IdRow>>(
            $"/rest/v1/profiles?select=id&username=eq.{Uri.EscapeDataString(username)}", ct).ConfigureAwait(false);
        if (profiles is not { Count: > 0 }) return null;
        var channelId = profiles[0].Id;
        var streams = await GetAsync<List<StreamInfo>>(
            $"/rest/v1/live_streams?select=id,status,chat_cleared_at,started_at,ended_at&channel_id=eq.{channelId}", ct).ConfigureAwait(false);
        if (streams is not { Count: > 0 }) return new StreamInfo { ChannelId = channelId };
        streams[0].ChannelId = channelId;
        return streams[0];
    }

    /// <summary>Mensajes en orden cronológico. <paramref name="after"/> es el created_at exacto del último ya mostrado.</summary>
    public async Task<List<ChatMessage>> GetMessagesAsync(string streamId, string? notBefore, string? after, int limit, CancellationToken ct)
    {
        var sb = new StringBuilder($"/rest/v1/live_chat_messages?select={MessageSelect}&stream_id=eq.{streamId}");
        if (after != null) sb.Append("&created_at=gt.").Append(Uri.EscapeDataString(after));
        else if (notBefore != null) sb.Append("&created_at=gte.").Append(Uri.EscapeDataString(notBefore));
        sb.Append("&order=created_at.desc&limit=").Append(limit);
        var list = await GetAsync<List<ChatMessage>>(sb.ToString(), ct).ConfigureAwait(false) ?? [];
        list.Reverse();
        return list;
    }

    public async Task<Dictionary<string, Emote>> GetEmotesAsync(CancellationToken ct)
    {
        var list = await GetAsync<List<Emote>>("/rest/v1/channel_emotes?select=code,storage_path,is_animated&order=created_at", ct).ConfigureAwait(false) ?? [];
        var map = new Dictionary<string, Emote>(StringComparer.Ordinal);
        foreach (var e in list) map.TryAdd(e.Code, e);
        return map;
    }

    static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TransparentChatStarvios", "emotes");

    /// <summary>Devuelve la ruta local de cada imagen, descargándola una sola vez (los nombres en Starvios son UUID inmutables).</summary>
    public async Task<Dictionary<string, string>> EnsureImagesAsync(IReadOnlyCollection<string> storagePaths, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        var result = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var p in storagePaths)
        {
            var local = Path.Combine(CacheDir, p.Replace('/', '_'));
            if (File.Exists(local)) result[p] = local; else missing.Add(p);
        }
        if (missing.Count == 0) return result;

        // El bucket "media" es privado: se piden URLs firmadas en bloque, como hace la web.
        var body = JsonSerializer.Serialize(new { expiresIn = 600, paths = missing });
        using var res = await _http.PostAsync("/storage/v1/object/sign/media",
            new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var signed = await JsonSerializer.DeserializeAsync<List<SignedUrl>>(
            await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false) ?? [];
        foreach (var s in signed)
        {
            if (s.SignedURL == null || s.Path == null) continue;
            try
            {
                var bytes = await _http.GetByteArrayAsync("/storage/v1" + s.SignedURL, ct).ConfigureAwait(false);
                var local = Path.Combine(CacheDir, s.Path.Replace('/', '_'));
                await File.WriteAllBytesAsync(local, bytes, ct).ConfigureAwait(false);
                result[s.Path] = local;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return result;
    }

    /// <summary>
    /// Escucha Supabase Realtime (protocolo Phoenix) y avisa de cada cambio en los mensajes del directo.
    /// Se reconecta solo. Devuelve cuando se cancela.
    /// </summary>
    public async Task ListenAsync(string streamId, Action<string, ChatMessage?> onChange, Action<bool> onConnected, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.Zero; // usamos el heartbeat de Phoenix
            try
            {
                var url = Project.Replace("https://", "wss://") + $"/realtime/v1/websocket?apikey={PublicKey}&vsn=1.0.0";
                await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                var topic = $"realtime:live-chat:{streamId}";
                await SendAsync(ws, new
                {
                    topic,
                    @event = "phx_join",
                    @ref = "1",
                    payload = new
                    {
                        config = new
                        {
                            broadcast = new { self = false },
                            presence = new { key = "" },
                            postgres_changes = new[] { new { @event = "*", schema = "public", table = "live_chat_messages", filter = $"stream_id=eq.{streamId}" } }
                        },
                        access_token = PublicKey
                    }
                }, ct).ConfigureAwait(false);

                using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hb = Task.Run(async () =>
                {
                    int n = 2;
                    while (!hbCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(25), hbCts.Token).ConfigureAwait(false);
                        await SendAsync(ws, new { topic = "phoenix", @event = "heartbeat", payload = new { }, @ref = (n++).ToString() }, hbCts.Token).ConfigureAwait(false);
                    }
                }, hbCts.Token);

                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    Handle(ms.GetBuffer().AsSpan(0, (int)ms.Length), onChange, onConnected, ref delay);
                }
                hbCts.Cancel();
                try { await hb.ConfigureAwait(false); } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { }
            onConnected(false);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    static void Handle(ReadOnlySpan<byte> json, Action<string, ChatMessage?> onChange, Action<bool> onConnected, ref TimeSpan delay)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            var ev = root.GetProperty("event").GetString();
            if (ev == "system" && root.GetProperty("payload").TryGetProperty("status", out var st) && st.GetString() == "ok")
            {
                delay = TimeSpan.FromSeconds(2);
                onConnected(true);
            }
            else if (ev == "postgres_changes")
            {
                var data = root.GetProperty("payload").GetProperty("data");
                var type = data.GetProperty("type").GetString() ?? "";
                ChatMessage? rec = null;
                if (data.TryGetProperty("record", out var r) && r.ValueKind == JsonValueKind.Object)
                    rec = r.Deserialize<ChatMessage>();
                onChange(type, rec);
            }
        }
        catch { }
    }

    static Task SendAsync(ClientWebSocket ws, object msg, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg), WebSocketMessageType.Text, true, ct);

    public void Dispose() => _http.Dispose();

    sealed class IdRow { [JsonPropertyName("id")] public string Id { get; set; } = ""; }
    sealed class SignedUrl
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("signedURL")] public string? SignedURL { get; set; }
    }
}

public sealed class StreamInfo
{
    [JsonIgnore] public string ChannelId { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("chat_cleared_at")] public string? ChatClearedAt { get; set; }
    [JsonPropertyName("started_at")] public string? StartedAt { get; set; }
    [JsonPropertyName("ended_at")] public string? EndedAt { get; set; }
    public bool IsLive => Status == "live";
}

public sealed class ChatMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("user_id")] public string? UserId { get; set; }
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("coins")] public long Coins { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("badges")] public string[]? Badges { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("profiles")] public Profile? Profile { get; set; }
}

public sealed class Profile
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("chat_color")] public string? ChatColor { get; set; }
    [JsonPropertyName("is_verified")] public bool IsVerified { get; set; }
    [JsonPropertyName("is_partner")] public bool IsPartner { get; set; }
    [JsonPropertyName("is_team")] public bool IsTeam { get; set; }
}

public sealed class Emote
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("storage_path")] public string StoragePath { get; set; } = "";
    [JsonPropertyName("is_animated")] public bool IsAnimated { get; set; }
}
