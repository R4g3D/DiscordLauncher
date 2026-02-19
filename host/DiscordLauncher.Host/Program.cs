using System.Diagnostics;
using System.Drawing;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DiscordLauncher.Host;

internal static class Program
{
    private const string ActionUuid = "com.kenobi.discordlauncher.launch";
    private const int OfflineState = 0;
    private const int RunningState = 1;
    private const int PollIntervalMs = 3000;

    private static readonly HashSet<string> Contexts = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim MessageLock = new(1, 1);

    private static ClientWebSocket? _socket;
    private static string _pluginUuid = string.Empty;
    private static string _registerEvent = string.Empty;
    private static int _port;

    private static string? _normalImageDataUrl;
    private static string? _runningImageDataUrl;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var parsed = ParseArgs(args);
            if (!parsed.TryGetValue("-port", out var portValue) || string.IsNullOrWhiteSpace(portValue) || !int.TryParse(portValue, out _port))
            {
                return 1;
            }

            if (!parsed.TryGetValue("-pluginUUID", out var pluginUuidValue) || string.IsNullOrWhiteSpace(pluginUuidValue))
            {
                return 1;
            }
            _pluginUuid = pluginUuidValue;

            if (!parsed.TryGetValue("-registerEvent", out var registerEventValue) || string.IsNullOrWhiteSpace(registerEventValue))
            {
                return 1;
            }
            _registerEvent = registerEventValue;

            await ConnectAndRunAsync();
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static async Task ConnectAndRunAsync()
    {
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}"), CancellationToken.None);
        await SendJsonAsync(new
        {
            @event = _registerEvent,
            uuid = _pluginUuid
        });

        _ = Task.Run(async () =>
        {
            while (_socket is { State: WebSocketState.Open })
            {
                try
                {
                    await RefreshAllContextStatesAsync();
                }
                catch
                {
                    // Ignore poll errors and keep running.
                }

                await Task.Delay(PollIntervalMs);
            }
        });

        await ReceiveLoopAsync();
    }

    private static async Task ReceiveLoopAsync()
    {
        if (_socket is null)
        {
            return;
        }

        var buffer = new byte[16 * 1024];
        var textBuilder = new StringBuilder();

        while (_socket.State == WebSocketState.Open)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                return;
            }

            textBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var json = textBuilder.ToString();
            textBuilder.Clear();

            await HandleMessageAsync(json);
        }
    }

    private static async Task HandleMessageAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("event", out var eventElement))
        {
            return;
        }

        var eventName = eventElement.GetString();
        if (!root.TryGetProperty("action", out var actionElement))
        {
            return;
        }

        var action = actionElement.GetString();
        if (!string.Equals(action, ActionUuid, StringComparison.Ordinal))
        {
            return;
        }

        var context = root.TryGetProperty("context", out var contextElement) ? contextElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(context))
        {
            return;
        }

        switch (eventName)
        {
            case "willAppear":
                Contexts.Add(context);
                await EnsureImagesAsync();
                await ApplyContextImagesAsync(context);
                await RefreshContextStateAsync(context);
                break;
            case "willDisappear":
                Contexts.Remove(context);
                break;
            case "keyDown":
                await HandleKeyDownAsync(context);
                break;
        }
    }

    private static async Task HandleKeyDownAsync(string context)
    {
        try
        {
            await EnsureImagesAsync();
            await ApplyContextImagesAsync(context);

            var broughtForward = BringDiscordToFrontOrLaunch();
            if (!broughtForward)
            {
                await ShowAlertAsync(context);
                await RefreshAllContextStatesAsync();
                return;
            }

            await ShowOkAsync(context);
            await RefreshAllContextStatesAsync();
        }
        catch
        {
            await ShowAlertAsync(context);
            await RefreshAllContextStatesAsync();
        }
    }

    private static bool LaunchDiscord()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var updaterPath = Path.Combine(localAppData, "Discord", "Update.exe");
        if (!File.Exists(updaterPath))
        {
            return false;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = "--processStart Discord.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        return process is not null;
    }

    private static bool BringDiscordToFrontOrLaunch()
    {
        if (FocusDiscordWindow())
        {
            return true;
        }

        // If Discord is in tray/background (or not running), this nudges/starts it through updater.
        if (!LaunchDiscord())
        {
            return false;
        }

        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(200);
            if (FocusDiscordWindow())
            {
                return true;
            }
        }

        return false;
    }

    private static bool FocusDiscordWindow()
    {
        var process = FindDiscordWindowProcess();
        if (process is null || process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        // Only restore if minimized. Forcing SW_RESTORE on a visible window can
        // break Discord's fullscreen/window state.
        if (IsIconic(process.MainWindowHandle))
        {
            _ = ShowWindowAsync(process.MainWindowHandle, 9); // SW_RESTORE
        }
        Thread.Sleep(120);
        _ = SetForegroundWindow(process.MainWindowHandle);
        return true;
    }

    private static Process? FindDiscordWindowProcess()
    {
        var byName = Process.GetProcessesByName("Discord");
        foreach (var process in byName)
        {
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process;
            }
        }

        return null;
    }

    private static bool IsDiscordRunning()
    {
        return Process.GetProcessesByName("Discord").Length > 0;
    }

    private static async Task EnsureImagesAsync()
    {
        if (!string.IsNullOrWhiteSpace(_normalImageDataUrl) && !string.IsNullOrWhiteSpace(_runningImageDataUrl))
        {
            return;
        }

        var discordBinary = ResolveDiscordBinaryPath();
        if (discordBinary is null)
        {
            return;
        }

        using var source = ExtractDiscordIconBitmap(discordBinary, 256);
        if (source is null)
        {
            return;
        }

        using var normal = new Bitmap(144, 144);
        using (var graphics = Graphics.FromImage(normal))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(source, new Rectangle(0, 0, 144, 144));
        }

        using var running = new Bitmap(normal);
        using (var graphics = Graphics.FromImage(running))
        using (var badgeBrush = new SolidBrush(Color.FromArgb(43, 172, 119)))
        using (var badgePen = new Pen(Color.Black, 4.0f))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const int size = 144;
            var badgeDiameter = (int)Math.Round(size * 0.255);
            var badgeInset = (int)Math.Round(size * 0.065);
            var badgeX = size - badgeDiameter - badgeInset;
            var badgeY = size - badgeDiameter - badgeInset;
            graphics.FillEllipse(badgeBrush, badgeX, badgeY, badgeDiameter, badgeDiameter);
            graphics.DrawEllipse(badgePen, badgeX, badgeY, badgeDiameter, badgeDiameter);
        }

        _normalImageDataUrl = BitmapToDataUrl(normal);
        _runningImageDataUrl = BitmapToDataUrl(running);
    }

    private static string BitmapToDataUrl(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return $"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}";
    }

    private static Bitmap? ExtractDiscordIconBitmap(string binaryPath, int size)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];
        var extracted = PrivateExtractIcons(binaryPath, 0, size, size, handles, ids, 1, 0);
        if (extracted > 0 && handles[0] != IntPtr.Zero)
        {
            try
            {
                using var icon = Icon.FromHandle(handles[0]);
                return new Bitmap(icon.ToBitmap());
            }
            finally
            {
                _ = DestroyIcon(handles[0]);
            }
        }

        using var fallback = Icon.ExtractAssociatedIcon(binaryPath);
        return fallback is null ? null : new Bitmap(fallback.ToBitmap());
    }

    private static string? ResolveDiscordBinaryPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var discordRoot = Path.Combine(localAppData, "Discord");
        if (!Directory.Exists(discordRoot))
        {
            return null;
        }

        var appDirs = Directory.GetDirectories(discordRoot, "app-*")
            .OrderByDescending(Path.GetFileName)
            .ToList();

        foreach (var dir in appDirs)
        {
            var exe = Path.Combine(dir, "Discord.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        var updater = Path.Combine(discordRoot, "Update.exe");
        return File.Exists(updater) ? updater : null;
    }

    private static async Task RefreshAllContextStatesAsync()
    {
        if (Contexts.Count == 0)
        {
            return;
        }

        var state = IsDiscordRunning() ? RunningState : OfflineState;
        foreach (var context in Contexts.ToArray())
        {
            await SetStateAsync(context, state);
        }
    }

    private static async Task RefreshContextStateAsync(string context)
    {
        var state = IsDiscordRunning() ? RunningState : OfflineState;
        await SetStateAsync(context, state);
    }

    private static async Task ApplyContextImagesAsync(string context)
    {
        if (string.IsNullOrWhiteSpace(_normalImageDataUrl) || string.IsNullOrWhiteSpace(_runningImageDataUrl))
        {
            return;
        }

        await SetImageAsync(context, _normalImageDataUrl, OfflineState);
        await SetImageAsync(context, _runningImageDataUrl, RunningState);
    }

    private static async Task ShowOkAsync(string context)
    {
        await SendJsonAsync(new
        {
            @event = "showOk",
            context
        });
    }

    private static async Task ShowAlertAsync(string context)
    {
        await SendJsonAsync(new
        {
            @event = "showAlert",
            context
        });
    }

    private static async Task SetStateAsync(string context, int state)
    {
        await SendJsonAsync(new
        {
            @event = "setState",
            context,
            payload = new
            {
                state
            }
        });
    }

    private static async Task SetImageAsync(string context, string image, int state)
    {
        await SendJsonAsync(new
        {
            @event = "setImage",
            context,
            payload = new
            {
                image,
                target = 0,
                state
            }
        });
    }

    private static async Task SendJsonAsync<T>(T value)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(value);
        var bytes = Encoding.UTF8.GetBytes(json);

        await MessageLock.WaitAsync();
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            MessageLock.Release();
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            parsed[args[i]] = args[i + 1];
        }

        return parsed;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        IntPtr[] phicon,
        int[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
