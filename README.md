<p align="center"><img src="icon.png" width="96" alt=""></p>

# Transparent Chat Starvios

Chat de [Starvios](https://starvios.com) **transparente y siempre encima** del juego, para streamers con un solo monitor. Lo pones encima del juego en ventana o en ventana sin bordes, lo bloqueas y el ratón lo atraviesa como si no estuviera.

No sale en Alt+Tab ni en la barra de tareas y nunca le quita el foco al juego: vive en los iconos ocultos de la bandeja.

> Funciona con juegos en **ventana** o **ventana sin bordes**. Ninguna ventana se puede poner encima de un juego en pantalla completa exclusiva.

Es una versión para Starvios de [Transparent Twitch Chat Overlay](https://github.com/baffler/Transparent-Twitch-Chat-Overlay) de baffler, rehecha desde cero para que gaste lo mínimo.

## Por qué gasta tan poco

El original carga una página web dentro de un navegador embebido (WebView2), y eso arranca varios procesos de Edge. Esta versión **no usa ningún navegador**:

- Lee el chat directamente de la API pública de Starvios y recibe los mensajes nuevos al momento, por WebSocket.
- Pinta los mensajes con controles nativos de Windows (WPF) y lo hace por CPU, así no le quita GPU al juego.
- Corre con prioridad baja, guarda en disco los emotes que ya ha bajado y devuelve la memoria que no usa.
- En reposo usa 0 % de CPU, porque solo redibuja cuando llega un mensaje.
- El ejecutable pesa unos 280 KB.

Medido en un PC con Windows 11 y el chat de un directo abierto: **unos 57 MB de RAM y 0,00 % de CPU en reposo**.

## Instalación

1. Instala [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) si no lo tienes (en la mayoría de PC con Windows 11 ya viene).
2. Descarga `TransparentChatStarvios.exe` de [Releases](../../releases) y ábrelo. No hace falta instalar nada más.
3. Pulsa **⚙** y escribe tu canal: vale el nombre (`natu`) o el enlace del chat (`https://starvios.com/popout/chat/natu`).

> La primera vez Windows puede enseñar el aviso «Windows protegió su PC». Pulsa «Más información» y luego «Ejecutar de todas formas».

## Uso

| Acción | Cómo |
|---|---|
| Mover | Arrastra la barra de arriba |
| Cambiar el tamaño | Arrastra la esquina de abajo a la derecha |
| **Bloquear** (el ratón lo atraviesa y la barra desaparece) | Botón 🔒 |
| **Desbloquear** | Un clic en su icono de la bandeja (iconos ocultos, junto al reloj) o **Ctrl + Shift + F12** |
| Menú (bloquear, ocultar/mostrar, ajustes, salir) | Clic derecho en el icono de la bandeja |
| Ajustes | Botón ⚙ |

En los ajustes puedes cambiar:

- El canal.
- La fuente (cualquiera de las instaladas) y si los nombres van en negrita.
- El tamaño de la letra.
- La opacidad del fondo, de 0 a 100 %.
- Ocultar los mensajes pasados unos segundos.
- Cuántos mensajes se ven a la vez.
- Si se muestran insignias y emotes.
- La sombra del texto.

Los ajustes y la posición de la ventana se guardan en `%APPDATA%\TransparentChatStarvios\settings.json`.

## Qué muestra

- El color de nombre de cada usuario.
- Las insignias: 👑 dueño, ⚡ staff o admin, 🛡 moderador, ♦ VIP, ♥ suscriptor, y ✔ en las cuentas verificadas.
- Los emotes de Starvios (`:código:`). Los animados se ven como imagen fija.
- Suscripciones, raids, Lluvias de Estrellas y donaciones de Starvies, resaltados.
- Los mensajes del sistema de Starvios.
- Los mensajes que borra un moderador desaparecen al momento.
- Igual que en la web: cuando el directo lleva más de 15 minutos terminado, deja de enseñar el chat viejo.

Es **solo de lectura**: no inicia sesión, no pide contraseñas y no puede escribir en el chat.

## Compilar

Hace falta el [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
./build.ps1        # deja dist/TransparentChatStarvios.exe
```

## Licencia

GPL-3.0, la misma que el proyecto original. Consulta [LICENSE](LICENSE).
Proyecto independiente, sin relación oficial con Starvios ni con Twitch.
