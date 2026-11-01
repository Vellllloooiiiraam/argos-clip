using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ArgosClip;

/// <summary>
/// Janela nativa do app: hospeda a interface web (servida pelo Kestrel local)
/// dentro de um WebView2, sem barra de endereço, abas ou cara de navegador.
/// </summary>
internal sealed class MainWindow : Form
{
    private static readonly Color Bg = Color.FromArgb(0x0b, 0x0b, 0x12);

    private readonly string _url;
    private readonly WebView2 _web = new()
    {
        Dock = DockStyle.Fill,
        DefaultBackgroundColor = Bg, // evita o "flash" branco antes da página carregar
    };

    private sealed record WinState(int X, int Y, int W, int H, bool Max);

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArgosClip", "window.json");

    public static void Run(string url)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainWindow(url));
    }

    private MainWindow(string url)
    {
        _url = url;

        Text = "Argos Clip";
        BackColor = Bg;
        MinimumSize = new Size(900, 640);
        Size = new Size(1220, 820);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* sem ícone */ }

        Controls.Add(_web);
        RestoreState();
        FormClosing += (_, _) => SaveState();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Barra de título escura combinando com o tema do app (Win10 20H1+ / Win11).
        var on = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
        }
        // Win11: cor da barra de título = fundo do app (COLORREF 0x00BBGGRR).
        var caption = 0x120B0B;
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            await InitWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var r = MessageBox.Show(
                "O Argos Clip precisa do \"Microsoft Edge WebView2 Runtime\" para abrir sua janela.\n\n" +
                "Sim = abrir a página de download do WebView2 (gratuito)\n" +
                "Não = abrir o Argos Clip no navegador por enquanto",
                "Argos Clip", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
            {
                OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            }
            FallbackToBrowser();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível iniciar a janela: " + ex.Message +
                "\n\nO Argos Clip vai abrir no navegador.", "Argos Clip",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            FallbackToBrowser();
        }
    }

    private async Task InitWebViewAsync()
    {
        // Perfil do WebView2 em %LocalAppData% (gravável mesmo se o app estiver em Program Files).
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArgosClip", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPinchZoomEnabled = false;
#if !DEBUG
        s.AreDevToolsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
#endif

        // Só a interface local fica dentro da janela; links externos vão para o navegador padrão.
        core.NavigationStarting += (_, e) =>
        {
            if (!IsLocal(e.Uri))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };

        // "⬇ Baixar clipe": pergunta onde salvar, como um app normal.
        core.DownloadStarting += OnDownloadStarting;

        core.Navigate(_url);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        BeginInvoke(new Action(() =>
        {
            try
            {
                var suggested = Path.GetFileName(e.ResultFilePath);
                var ext = Path.GetExtension(suggested);
                using var dlg = new SaveFileDialog
                {
                    Title = "Salvar clipe",
                    FileName = suggested,
                    InitialDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    OverwritePrompt = true,
                    Filter = string.IsNullOrEmpty(ext)
                        ? "Todos os arquivos|*.*"
                        : $"Arquivo {ext.TrimStart('.').ToUpperInvariant()} (*{ext})|*{ext}|Todos os arquivos|*.*",
                };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    e.ResultFilePath = dlg.FileName;
                    e.Handled = true; // já escolhemos o destino; sem o diálogo padrão do WebView2
                }
                else
                {
                    e.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        }));
    }

    private void FallbackToBrowser()
    {
        Controls.Remove(_web);
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.Gainsboro,
            BackColor = Bg,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f),
            Text = "O Argos Clip está rodando no navegador.\nFeche esta janela para encerrar o app.",
        });
        OpenExternal(_url);
    }

    private static bool IsLocal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        return u.Scheme is "about" or "data" || u.Host == "127.0.0.1";
    }

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* sem navegador padrão */ }
    }

    // ---- lembrar tamanho/posição da janela ----------------------------------

    private void RestoreState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var st = JsonSerializer.Deserialize<WinState>(File.ReadAllText(StatePath));
            if (st is null) return;
            var r = new Rectangle(st.X, st.Y, st.W, st.H);
            if (r.Width < MinimumSize.Width || r.Height < MinimumSize.Height) return;
            if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) return; // monitor sumiu
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
            if (st.Max) WindowState = FormWindowState.Maximized;
        }
        catch
        {
            // estado inválido: usa o padrão
        }
    }

    private void SaveState()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(
                new WinState(b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized)));
        }
        catch
        {
            // melhor esforço
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
