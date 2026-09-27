using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaletteMap;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "PaletteMap.Instance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(showWindow: !args.Contains("--tray")));
    }
}

// Lancement à l'ouverture de session via le Planificateur de tâches, avec les droits administrateur
// (la clé Run ne peut pas lancer une application qui exige ces droits)
static class AutoStart
{
    const string Task = "PaletteMap";

    static int Schtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        return p.ExitCode;
    }

    public static bool Enabled
    {
        get => Schtasks($"/Query /TN {Task}") == 0;
        set
        {
            if (!value) { Schtasks($"/Delete /TN {Task} /F"); return; }

            // Pas d'arrêt sur batterie ni après 72 h, priorité normale (valeurs par défaut inadaptées)
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
                  <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>5</Priority>
                    <Enabled>true</Enabled>
                  </Settings>
                  <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(Environment.ProcessPath)}</Command><Arguments>--tray</Arguments></Exec></Actions>
                </Task>
                """;
            string file = Path.Combine(Path.GetTempPath(), "PaletteMap-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            Schtasks($"/Create /TN {Task} /XML \"{file}\" /F");
            File.Delete(file);
        }
    }
}

// Affectation d'une palette : boutons de manette (pressés dans l'ordre de Pad.Names) puis touche clavier avec modificateurs
sealed class Bind
{
    public List<string> Pad { get; set; } = new();
    public int Mods { get; set; }   // combinaison de Keys.Control, Keys.Shift, Keys.Alt
    public int Vk { get; set; }     // touche clavier principale, 0 = aucune

    [JsonIgnore] public bool IsEmpty => Pad.Count == 0 && Vk == 0;

    public Bind Copy() => new() { Pad = new(Pad), Mods = Mods, Vk = Vk };

    public override string ToString()
    {
        var parts = new List<string>(Pad);
        if ((Mods & (int)Keys.Control) != 0) parts.Add("Ctrl");
        if ((Mods & (int)Keys.Shift) != 0) parts.Add("Maj");
        if ((Mods & (int)Keys.Alt) != 0) parts.Add("Alt");
        if (Vk != 0) parts.Add(Native.KeyName(Vk));
        return parts.Count == 0 ? "Aucune" : string.Join(" + ", parts);
    }
}

// Affectations des 4 palettes, stockées dans %APPDATA%\PaletteMap\config.json
sealed class Config
{
    public Bind[] Binds { get; set; } = { new(), new(), new(), new() };
    public int[]? Vk { get; set; }   // ancien format (une touche par palette), converti au chargement

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaletteMap", "config.json");

    public static Config Load()
    {
        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
            if (c?.Vk?.Length == 4 && c.Binds.All(b => b.IsEmpty))
                for (int i = 0; i < 4; i++) c.Binds[i].Vk = c.Vk[i];
            if (c?.Binds.Length == 4) { c.Vk = null; return c; }
        }
        catch { }
        return new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

// Manette Xbox 360 virtuelle (driver ViGEmBus) : n'envoie que les boutons déclenchés par les palettes
static class Pad
{
    // Ordre d'appui : gâchettes et bumpers d'abord, pour qu'ils agissent comme modificateurs
    public static readonly string[] Names = { "LT", "RT", "LB", "RB", "A", "B", "X", "Y", "Haut", "Bas", "Gauche", "Droite", "L3", "R3", "Vue", "Menu" };
    static readonly Xbox360Button?[] Buttons =
    {
        null, null, Xbox360Button.LeftShoulder, Xbox360Button.RightShoulder, Xbox360Button.A, Xbox360Button.B, Xbox360Button.X, Xbox360Button.Y,
        Xbox360Button.Up, Xbox360Button.Down, Xbox360Button.Left, Xbox360Button.Right, Xbox360Button.LeftThumb, Xbox360Button.RightThumb,
        Xbox360Button.Back, Xbox360Button.Start
    };

    static ViGEmClient? client;
    static IXbox360Controller? pad;
    static readonly int[] count = new int[Names.Length];   // plusieurs palettes peuvent tenir le même bouton

    public static bool Available => pad != null;
    public static bool IsModifier(string name) => Array.IndexOf(Names, name) is >= 0 and < 4;   // LT, RT, LB, RB
    public static string Error { get; private set; } = "";

    public static void Init()
    {
        try
        {
            client = new ViGEmClient();
            pad = client.CreateXbox360Controller();
            pad.Connect();
        }
        catch (Exception e)   // seules les touches clavier restent alors disponibles
        {
            pad = null;
            Error = e is VigemBusNotFoundException ? "driver ViGEmBus non installé" : $"{e.GetType().Name} : {e.Message}";
        }
    }

    public static void Set(string name, bool down)
    {
        int i = Array.IndexOf(Names, name);
        if (pad == null || i < 0) return;
        lock (count)
        {
            if (down) { if (count[i]++ > 0) return; }
            else if (count[i] == 0 || --count[i] > 0) return;

            if (i == 0) pad.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)(down ? 255 : 0));
            else if (i == 1) pad.SetSliderValue(Xbox360Slider.RightTrigger, (byte)(down ? 255 : 0));
            else pad.SetButtonState(Buttons[i]!, down);
        }
    }
}

// Lit le trafic USB via USBPcap et extrait l'état des palettes du rapport d'entrée de l'Elite Series 1.
// Rapport GIP 0x20 de 33 octets (octet 3 = 0x1D) : l'octet 32 porte les palettes, relevé sur capture.
sealed class Mapper : IDisposable
{
    public static readonly string[] Names = { "Haut gauche", "Haut droit", "Bas gauche", "Bas droit" };
    static readonly int[] Bits = { 0x01, 0x02, 0x04, 0x08 };

    const int KernelBuffer = 1 << 20;
    const int Snaplen = 128;

    readonly Config cfg;
    readonly object gate = new();
    readonly bool[] pressed = new bool[4];
    readonly Bind?[] held = new Bind?[4];   // affectation réellement envoyée à l'appui, relâchée même si le réglage change entre-temps
    readonly List<SafeFileHandle> open = new();   // captures en cours, interrompues au réveil de Windows pour être rouvertes
    readonly int hubs;
    volatile bool detected, run = true;

    public event Action<int, bool>? PaddleChanged;

    public string Status =>
        hubs == 0 ? "USBPcap introuvable" :
        detected ? "Manette connectée" : "Appuie sur un bouton de la manette";

    public Mapper(Config cfg)
    {
        this.cfg = cfg;
        for (int i = 1; i <= 16; i++)
        {
            string path = $@"\\.\USBPcap{i}";
            var h = Native.OpenUsbPcap(path, Snaplen, KernelBuffer);
            if (h == null) continue;
            hubs++;
            new Thread(() => Run(path, h)) { IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start();
        }
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) Restart(); };
    }

    // Lit tant que la capture fonctionne, puis la rouvre (la mise en veille invalide la capture en cours)
    void Run(string path, SafeFileHandle? h)
    {
        while (run)
        {
            if (h != null)
            {
                lock (open) open.Add(h);
                Read(h);
                lock (open) open.Remove(h);
                h.Dispose();
                ReleaseAll();
            }
            Thread.Sleep(1000);
            h = Native.OpenUsbPcap(path, Snaplen, KernelBuffer);
        }
    }

    // Au réveil, interrompt les lectures en attente pour forcer la réouverture des captures
    void Restart()
    {
        lock (open) foreach (var h in open) Native.CancelIoEx(h, IntPtr.Zero);
    }

    void ReleaseAll()
    {
        lock (gate)
            for (int i = 0; i < 4; i++)
            {
                Release(i);
                if (pressed[i]) { pressed[i] = false; PaddleChanged?.Invoke(i, false); }
            }
    }

    // Flux au format pcap : en-tête global de 24 octets, puis enregistrements (en-tête 16 octets + paquet)
    void Read(SafeFileHandle h)
    {
        var chunk = new byte[KernelBuffer];
        var acc = new byte[KernelBuffer + 4096];
        int len = 0;
        bool header = true;

        while (run && Native.ReadFile(h, chunk, chunk.Length, out int n, IntPtr.Zero) && n > 0)
        {
            Buffer.BlockCopy(chunk, 0, acc, len, n);
            len += n;
            int pos = 0;

            if (header) { if (len < 24) continue; pos = 24; header = false; }

            while (len - pos >= 16)
            {
                int incl = BitConverter.ToInt32(acc, pos + 8);
                if (len - pos < 16 + incl) break;
                Packet(acc, pos + 16, incl);
                pos += 16 + incl;
            }
            Buffer.BlockCopy(acc, pos, acc, 0, len - pos);
            len -= pos;
        }
    }

    // En-tête USBPcap : info à l'octet 16 (bit 0 = venant du périphérique), endpoint 21, type de transfert 22 (1 = interruption)
    void Packet(byte[] b, int o, int n)
    {
        if (n < 27) return;
        int hl = BitConverter.ToUInt16(b, o);
        if ((b[o + 16] & 1) == 0 || b[o + 21] != 0x81 || b[o + 22] != 1) return;

        int d = o + hl;
        if (n - hl < 33 || b[d] != 0x20 || b[d + 3] != 0x1D) return;

        detected = true;
        int mask = b[d + 32];

        lock (gate)
        {
            for (int i = 0; i < 4; i++)
            {
                bool p = (mask & Bits[i]) != 0;
                if (p == pressed[i]) continue;
                pressed[i] = p;

                if (p) Press(i); else Release(i);

                PaddleChanged?.Invoke(i, p);
            }
        }
    }

    static readonly (int flag, int vk)[] Modifiers = { ((int)Keys.Control, 0x11), ((int)Keys.Shift, 0x10), ((int)Keys.Alt, 0x12) };

    // Appui : modificateurs manette (LT, RT, LB, RB), pause pour que WoW les voie maintenus,
    // puis autres boutons, modificateurs clavier et touche ; relâchement dans l'ordre inverse
    const int ModifierDelay = 25;   // ms

    void Press(int i)
    {
        var b = cfg.Binds[i].Copy();
        held[i] = b;
        int m = b.Pad.Count(Pad.IsModifier);   // en tête de liste (ordre de Pad.Names)
        bool rest = b.Pad.Count > m || b.Vk != 0 || b.Mods != 0;

        for (int k = 0; k < m; k++) Pad.Set(b.Pad[k], true);
        if (m > 0 && rest) Thread.Sleep(ModifierDelay);
        for (int k = m; k < b.Pad.Count; k++) Pad.Set(b.Pad[k], true);

        foreach (var (flag, vk) in Modifiers) if ((b.Mods & flag) != 0) Native.SendKey(vk, true);
        if (b.Vk != 0) Native.SendKey(b.Vk, true);
    }

    void Release(int i)
    {
        var b = held[i];
        if (b == null) return;
        held[i] = null;
        if (b.Vk != 0) Native.SendKey(b.Vk, false);
        for (int k = Modifiers.Length - 1; k >= 0; k--) if ((b.Mods & Modifiers[k].flag) != 0) Native.SendKey(Modifiers[k].vk, false);
        for (int k = b.Pad.Count - 1; k >= 0; k--) Pad.Set(b.Pad[k], false);
    }

    public void Dispose()
    {
        run = false;
        ReleaseAll();
    }
}

static class Native
{
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int lParam, StringBuilder buffer, int size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int size, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, OPEN_EXISTING = 3;

    // Codes IOCTL de USBPcap (USBPcapDriver/include/USBPcap.h) : CTL_CODE(FILE_DEVICE_UNKNOWN, fonction, METHOD_BUFFERED, accès)
    const uint IOCTL_SETUP_BUFFER = 0x226000, IOCTL_START_FILTERING = 0x22E004, IOCTL_SET_SNAPLEN = 0x226010;

    // Ouvre un concentrateur USBPcap et capture tous ses périphériques (y compris ceux branchés ensuite)
    public static SafeFileHandle? OpenUsbPcap(string path, int snaplen, int bufferSize)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        var filter = new byte[17];   // 4 x UINT32 d'adresses + BOOLEAN filterAll
        filter[16] = 1;

        if (DeviceIoControl(h, IOCTL_SET_SNAPLEN, BitConverter.GetBytes(snaplen), 4, IntPtr.Zero, 0, out _, IntPtr.Zero) &&
            DeviceIoControl(h, IOCTL_SETUP_BUFFER, BitConverter.GetBytes(bufferSize), 4, IntPtr.Zero, 0, out _, IntPtr.Zero) &&
            DeviceIoControl(h, IOCTL_START_FILTERING, filter, filter.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            return h;

        h.Dispose();
        return null;
    }

    // Touches dites "étendues" : flèches, bloc Inser/Suppr/Début/Fin/Page, Windows, Menu, / du pavé, Verr Num
    static readonly HashSet<int> Extended = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90 };

    // Envoi en scancode : reconnu par les jeux qui lisent le clavier en DirectInput / Raw Input
    public static void SendKey(int vk, bool down)
    {
        uint flags = KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKey((uint)vk, 0), dwFlags = flags } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // Nom de la touche selon la disposition du clavier (AZERTY : "MAJ", "ESPACE"...)
    public static string KeyName(int vk)
    {
        if (vk == 0) return "Aucune";
        int lParam = (int)(MapVirtualKey((uint)vk, 0) << 16) | (Extended.Contains(vk) ? 1 << 24 : 0);
        var sb = new StringBuilder(64);
        return GetKeyNameText(lParam, sb, sb.Capacity) > 0 ? sb.ToString() : ((Keys)vk).ToString();
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly Mapper mapper;
    readonly NotifyIcon tray;
    readonly SettingsForm form;

    public TrayApp(bool showWindow)
    {
        var cfg = Config.Load();
        Pad.Init();
        mapper = new Mapper(cfg);
        form = new SettingsForm(cfg, mapper);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Réglages", null, (_, _) => ShowForm());
        menu.Items.Add("Quitter", null, (_, _) => Quit());

        tray = new NotifyIcon { Icon = MakeIcon(), Text = "PaletteMap", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };

        if (showWindow) ShowForm();
    }

    void ShowForm()
    {
        form.Show();
        form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    void Quit()
    {
        tray.Visible = false;
        mapper.Dispose();
        form.AllowClose = true;
        form.Close();
        ExitThread();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(Brushes.Black, 2, 2, 28, 28);
            g.FillEllipse(Brushes.White, 11, 11, 10, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

sealed class SettingsForm : Form
{
    public bool AllowClose;

    static readonly Color Secondary = Color.FromArgb(110, 110, 115);

    readonly Config cfg;
    readonly Dot[] dots = new Dot[4];
    readonly Chip[] chips = new Chip[4];
    int capturing = -1;

    public SettingsForm(Config cfg, Mapper mapper)
    {
        this.cfg = cfg;

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "PaletteMap";
        Font = new Font("Segoe UI", 12f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 400);
        KeyPreview = true;

        Controls.Add(new Label { Text = "Palettes", Font = new Font("Segoe UI Semibold", 16.5f), Location = new Point(24, 16), AutoSize = true });

        var status = new Label { Font = new Font("Segoe UI", 10f), ForeColor = Secondary, Location = new Point(26, 58), AutoSize = true };
        Controls.Add(status);

        for (int i = 0; i < 4; i++)
        {
            int y = 96 + i * 52;
            int index = i;

            dots[i] = new Dot { Location = new Point(26, y + 18) };
            Controls.Add(dots[i]);
            Controls.Add(new Label { Text = Mapper.Names[i], Location = new Point(50, y + 12), AutoSize = true });

            chips[i] = new Chip { Location = new Point(200, y + 4), Size = new Size(176, 44) };
            chips[i].Click += (_, _) => ShowMenu(index);
            Controls.Add(chips[i]);
        }

        Controls.Add(new Label
        {
            Text = "Clic : boutons de manette et touche clavier.\nÉchap : annuler.  Suppr : retirer la touche clavier.",
            Font = new Font("Segoe UI", 9f), ForeColor = Secondary, Location = new Point(26, 312), AutoSize = true
        });

        var autoStart = new MouseOnlyCheckBox { Text = "Lancer au démarrage de Windows", Checked = AutoStart.Enabled, Location = new Point(26, 360), AutoSize = true, Cursor = Cursors.Hand };
        autoStart.CheckedChanged += (_, _) => AutoStart.Enabled = autoStart.Checked;
        Controls.Add(autoStart);

        UpdateChips();

        mapper.PaddleChanged += (i, p) =>
        {
            if (IsHandleCreated) BeginInvoke((MethodInvoker)(() => dots[i].On = p));
        };

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => status.Text = mapper.Status;
        timer.Start();
        status.Text = mapper.Status;
    }

    // Menu d'affectation : les boutons de manette se cochent sans fermer le menu, pour composer une combinaison (ex. LT + A)
    void ShowMenu(int i)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = Font };
        bool keepOpen = false;

        if (Pad.Available)
            for (int k = 0; k < Pad.Names.Length; k++)
            {
                if (k > 0 && k % 4 == 0) menu.Items.Add(new ToolStripSeparator());
                string name = Pad.Names[k];
                var item = new ToolStripMenuItem(name) { Checked = cfg.Binds[i].Pad.Contains(name), Tag = name };
                item.Click += (_, _) =>
                {
                    var b = cfg.Binds[i].Copy();
                    if (!b.Pad.Remove(name)) b.Pad.Add(name);
                    b.Pad = Pad.Names.Where(b.Pad.Contains).ToList();
                    Apply(i, b);
                    item.Checked = b.Pad.Contains(name);
                };
                menu.Items.Add(item);
            }
        else
            menu.Items.Add(new ToolStripMenuItem($"Manette virtuelle indisponible : {Pad.Error}") { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Touche clavier…", null, (_, _) => StartCapture(i));
        menu.Items.Add("Tout effacer", null, (_, _) => Apply(i, new Bind()));

        menu.ItemClicked += (_, e) => keepOpen = e.ClickedItem?.Tag is string;
        menu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && keepOpen) e.Cancel = true; };
        menu.Closed += (_, _) => BeginInvoke((MethodInvoker)menu.Dispose);
        menu.Show(chips[i], new Point(0, chips[i].Height));
    }

    // Remplace l'affectation d'un bloc (le fil de lecture USB ne voit jamais un objet à moitié modifié)
    void Apply(int i, Bind b)
    {
        cfg.Binds[i] = b;
        cfg.Save();
        UpdateChips();
    }

    void StartCapture(int i)
    {
        capturing = i;
        UpdateChips();
    }

    void UpdateChips()
    {
        for (int i = 0; i < 4; i++)
        {
            chips[i].Active = i == capturing;
            chips[i].Text = i == capturing ? "Touche ?" : cfg.Binds[i].ToString();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (capturing < 0) return base.ProcessCmdKey(ref msg, keyData);

        var key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return true;   // attendre la touche principale (voir OnKeyUp)

        int i = capturing;
        capturing = -1;
        var b = cfg.Binds[i].Copy();

        if (key == Keys.Delete) { b.Vk = 0; b.Mods = 0; }
        else if (key != Keys.Escape) { b.Vk = (int)key; b.Mods = (int)(keyData & Keys.Modifiers); }

        Apply(i, b);
        return true;
    }

    // Ctrl, Maj ou Alt relâché seul pendant la capture : il devient la touche affectée
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (capturing < 0 || e.KeyCode is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu)) return;

        int i = capturing;
        capturing = -1;
        var b = cfg.Binds[i].Copy();
        b.Vk = (int)e.KeyCode;
        b.Mods = 0;
        Apply(i, b);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            capturing = -1;
            UpdateChips();
            Hide();
        }
        base.OnFormClosing(e);
    }
}

// Case à cocher sans focus clavier : les touches envoyées par les palettes ne peuvent pas la basculer
sealed class MouseOnlyCheckBox : CheckBox
{
    public MouseOnlyCheckBox() => SetStyle(ControlStyles.Selectable, false);
}

// Témoin d'appui : cercle vide au repos, plein quand la palette est enfoncée
sealed class Dot : Control
{
    bool on;
    public bool On { get => on; set { if (on == value) return; on = value; Invalidate(); } }

    public Dot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(12, 12);
        BackColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (on) e.Graphics.FillEllipse(Brushes.Black, r);
        else using (var pen = new Pen(Color.FromArgb(190, 190, 195))) e.Graphics.DrawEllipse(pen, r);
    }
}

// Bouton capsule affichant la touche affectée
sealed class Chip : Control
{
    bool active;
    public bool Active { get => active; set { active = value; Invalidate(); } }

    public Chip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Cursor = Cursors.Hand;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int d = Height - 1;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 90, 180);
        path.AddArc(Width - 1 - d, 0, d, d, 270, 180);
        path.CloseFigure();

        using var fill = new SolidBrush(active ? Color.Black : Color.FromArgb(242, 242, 245));
        g.FillPath(fill, path);

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, active ? Color.White : Color.Black,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
