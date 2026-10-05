using System.Windows;

namespace TransparentChatStarvios;

public partial class SettingsWindow : Window
{
    readonly Settings _s;

    public SettingsWindow(Settings s)
    {
        InitializeComponent();
        _s = s;
        Channel.Text = s.Channel;
        var fonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        FontPicker.ItemsSource = fonts;
        FontPicker.SelectedItem = fonts.FirstOrDefault(f => string.Equals(f, s.FontFamily, StringComparison.OrdinalIgnoreCase)) ?? "Segoe UI";
        BoldNames.IsChecked = s.BoldNames;
        FontSlider.Value = s.FontSize;
        OpacitySlider.Value = Math.Round(s.BackgroundOpacity * 100);
        HideAfter.Value = s.HideAfterSeconds;
        MaxMessages.Value = s.MaxMessages;
        ShowBadges.IsChecked = s.ShowBadges;
        ShowEmotes.IsChecked = s.ShowEmotes;
        TextShadow.IsChecked = s.TextShadow;

        FontSlider.ValueChanged += (_, _) => Labels();
        OpacitySlider.ValueChanged += (_, _) => Labels();
        HideAfter.ValueChanged += (_, _) => Labels();
        MaxMessages.ValueChanged += (_, _) => Labels();
        Labels();
        Loaded += (_, _) => { Channel.Focus(); Channel.SelectAll(); };
    }

    void Labels()
    {
        FontLabel.Text = $"Tamaño de letra: {FontSlider.Value:0}";
        OpacityLabel.Text = $"Opacidad del fondo: {OpacitySlider.Value:0} %";
        HideLabel.Text = HideAfter.Value == 0 ? "Ocultar mensajes: nunca" : $"Ocultar mensajes tras {HideAfter.Value:0} s";
        MaxLabel.Text = $"Máximo de mensajes en pantalla: {MaxMessages.Value:0}";
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.Channel = Settings.NormalizeChannel(Channel.Text);
        _s.FontFamily = FontPicker.SelectedItem as string ?? "Segoe UI";
        _s.BoldNames = BoldNames.IsChecked == true;
        _s.FontSize = FontSlider.Value;
        _s.BackgroundOpacity = OpacitySlider.Value / 100.0;
        _s.HideAfterSeconds = (int)HideAfter.Value;
        _s.MaxMessages = (int)MaxMessages.Value;
        _s.ShowBadges = ShowBadges.IsChecked == true;
        _s.ShowEmotes = ShowEmotes.IsChecked == true;
        _s.TextShadow = TextShadow.IsChecked == true;
        DialogResult = true;
    }
}
