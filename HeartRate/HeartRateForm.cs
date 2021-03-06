﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HeartRate
{
    public partial class HeartRateForm : Form
    {
        // Excessively call the main rendering function to force any leaks that
        // could happen.
        private const bool _leaktest = false;

        private readonly IHeartRateService _service;
        private readonly object _disposeSync = new object();
        private readonly object _updateSync = new object();
        private readonly Bitmap _iconBitmap;
        private readonly Graphics _iconGraphics;
        private readonly HeartRateSettings _settings;
        private readonly int _iconWidth = GetSystemMetrics(SystemMetric.SmallIconX);
        private readonly int _iconHeight = GetSystemMetrics(SystemMetric.SmallIconY);
        private readonly StringFormat _iconStringFormat = new StringFormat {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        private readonly Font _measurementFont;
        private readonly Stopwatch _alertTimeout = new Stopwatch();
        private readonly Stopwatch _disconnectedTimeout = new Stopwatch();
        private readonly DateTime _startedAt;
        private readonly HeartRateServiceWatchdog _watchdog;
        private LogFile _log;
        private IBIFile _ibi;
        private HeartRateSettings _lastSettings;

        private string _iconText;
        private Font _lastFont;
        private IntPtr _oldIconHandle;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(SystemMetric nIndex);

        [DllImport("user32.dll")]
        private static extern int SetForegroundWindow(int hWnd);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private enum SystemMetric
        {
            SmallIconX = 49, // SM_CXSMICON
            SmallIconY = 50, // SM_CYSMICON
        }

        public HeartRateForm() : this(
            Environment.CommandLine.Contains("--test")
                ? (IHeartRateService)new TestHeartRateService()
                : new HeartRateService(),
            HeartRateSettings.GetFilename(),
            DateTime.Now)
        {
        }

        internal HeartRateForm(
            IHeartRateService service,
            string settingsFilename,
            DateTime now)
        {
            try
            {
                // Order of operations -- _startedAt has to be set before
                // `LoadSettingsLocked` is called.
                _startedAt = now;

                _settings = HeartRateSettings.CreateDefault(settingsFilename);
                LoadSettingsLocked();
                _settings.Save();
                _service = service;
                _iconBitmap = new Bitmap(_iconWidth, _iconHeight);
                _iconGraphics = Graphics.FromImage(_iconBitmap);
                _measurementFont = new Font(
                    _settings.FontName, _iconWidth,
                    GraphicsUnit.Pixel);
                _watchdog = new HeartRateServiceWatchdog(
                    TimeSpan.FromSeconds(10), _service);

                InitializeComponent();

                FormBorderStyle = _settings.Sizable
                    ? FormBorderStyle.Sizable
                    : FormBorderStyle.SizableToolWindow;
            }
            catch
            {
                service.TryDispose();
                throw;
            }
        }

        private void HeartRateForm_Load(object sender, EventArgs e)
        {
            UpdateLabelFont();
            Hide();

            try
            {
                // InitiateDefault is blocking. A better UI would show some type
                // of status during this time, but it's not super important.
                _service.InitiateDefault();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to initialize bluetooth service. Exiting.\n{ex.Message}",
                    "Fatal exception",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);

                Environment.Exit(-1);
            }

            _service.HeartRateUpdated += Service_HeartRateUpdated;

            UpdateUI();
        }

        private void Service_HeartRateUpdated(HeartRateReading reading)
        {
            try
            {
                if (_leaktest)
                {
                    for (var i = 0; i < 4000; ++i)
                    {
                        Service_HeartRateUpdatedCore(reading);
                    }

                    return;
                }

                Service_HeartRateUpdatedCore(reading);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in Service_HeartRateUpdated {ex}");

                Debugger.Break();
            }
        }

        private void Service_HeartRateUpdatedCore(HeartRateReading reading)
        {
            _log?.Reading(reading);
            _ibi?.Reading(reading);

            var bpm = reading.BeatsPerMinute;
            var status = reading.Status;

            var isDisconnected = bpm == 0 ||
                status == ContactSensorStatus.NoContact;

            var iconText = bpm.ToString();

            var warnLevel = _settings.WarnLevel;
            var alertLevel = _settings.AlertLevel;
            // <= 0 implies disabled.
            var isWarn = warnLevel > 0 && bpm >= warnLevel;
            var isAlert = alertLevel > 0 && bpm >= alertLevel;

            lock (_updateSync)
            {
                if (isDisconnected)
                {
                    uxBpmNotifyIcon.Text = $"Disconnected {status} ({bpm})";

                    if (!_disconnectedTimeout.IsRunning)
                    {
                        _disconnectedTimeout.Start();
                    }

                    if (_disconnectedTimeout.Elapsed >
                        _settings.DisconnectedTimeout)
                    {
                        // Originally this used " ⃠" (U+20E0, "Prohibition Symbol")
                        // but MeasureString was only returning ~half of the
                        // width.
                        iconText = "X";
                    }
                }
                else
                {
                    uxBpmNotifyIcon.Text = null;
                    _disconnectedTimeout.Stop();
                }

                _iconGraphics.Clear(Color.Transparent);

                var sizingMeasurement = _iconGraphics
                    .MeasureString(iconText, _measurementFont);

                var color = isWarn ? _settings.WarnColor : _settings.Color;

                using (var brush = new SolidBrush(color))
                using (var font = new Font(_settings.FontName,
                    _iconHeight * (_iconWidth / sizingMeasurement.Width),
                    GraphicsUnit.Pixel))
                {
                    _iconGraphics.DrawString(
                        iconText, font, brush,
                        new RectangleF(0, 0, _iconWidth, _iconHeight),
                        _iconStringFormat);
                }

                _iconText = iconText;

                Invoke(new Action(() => {
                    uxBpmLabel.Text = _iconText;
                    uxBpmLabel.ForeColor = isWarn
                        ? _settings.UIWarnColor
                        : _settings.UIColor;

                    UpdateUICore();
                }));

                var iconHandle = _iconBitmap.GetHicon();

                using (var icon = Icon.FromHandle(iconHandle))
                {
                    uxBpmNotifyIcon.Icon = icon;

                    if (isAlert && (!_alertTimeout.IsRunning ||
                        _alertTimeout.Elapsed >= _settings.AlertTimeout))
                    {
                        _alertTimeout.Restart();

                        var alertText = $"BPMs @ {bpm}";

                        uxBpmNotifyIcon.ShowBalloonTip(
                            (int)_settings.AlertTimeout.TotalMilliseconds,
                            alertText, alertText, ToolTipIcon.Warning);
                    }
                }

                if (_oldIconHandle != IntPtr.Zero)
                {
                    DestroyIcon(_oldIconHandle);
                }

                _oldIconHandle = iconHandle;
            }
        }

        private void UpdateUI()
        {
            Invoke(new Action(UpdateUICore));
        }

        private void UpdateUICore()
        {
            uxBpmLabel.BackColor = _settings.UIBackgroundColor;

            var fontx = _settings.UIFontName;

            if (uxBpmLabel.Font.FontFamily.Name != fontx)
            {
                UpdateLabelFontLocked();
            }

            if (_lastSettings?.UIBackgroundFile != _settings.UIBackgroundFile)
            {
                var oldBackgroundImage = uxBpmLabel.BackgroundImage;
                var backgroundFile = _settings.UIBackgroundFile;

                if (!string.IsNullOrWhiteSpace(backgroundFile) &&
                    File.Exists(backgroundFile))
                {
                    try
                    {
                        var image = Image.FromFile(backgroundFile);
                        uxBpmLabel.BackgroundImage = image;
                        oldBackgroundImage.TryDispose();
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show($"Unable to load background image file \"{backgroundFile}\" due to error: {e}");
                    }
                }
                else
                {
                    uxBpmLabel.BackgroundImage = null;
                    oldBackgroundImage.TryDispose();
                }
            }

            if (_lastSettings?.UIBackgroundLayout != _settings.UIBackgroundLayout)
            {
                uxBpmLabel.BackgroundImageLayout = _settings.UIBackgroundLayout;
            }

            _lastSettings = _settings.Clone();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                components.TryDispose();

                lock (_disposeSync)
                {
                    _service.TryDispose();
                    _iconBitmap.TryDispose();
                    _iconGraphics.TryDispose();
                    _measurementFont.TryDispose();
                    _iconStringFormat.TryDispose();
                    _watchdog.TryDispose();
                }
            }

            base.Dispose(disposing);
        }

        private void uxBpmNotifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Show();
                SetForegroundWindow(Handle.ToInt32());
            }
        }

        private void HeartRateForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void HeartRateForm_ResizeEnd(object sender, EventArgs e)
        {
            UpdateLabelFont();
            UpdateUI();
        }

        private void UpdateLabelFont()
        {
            lock (_updateSync)
            {
                UpdateLabelFontLocked();
            }
        }

        private void UpdateLabelFontLocked()
        {
            var newFont = new Font(
                _settings.UIFontName, uxBpmLabel.Height,
                GraphicsUnit.Pixel);

            uxBpmLabel.Font = newFont;
            _lastFont.TryDispose();
            _lastFont = newFont;
        }

        private void uxMenuEditSettings_Click(object sender, EventArgs e)
        {
            var thread = new Thread(() => {
                using (var process = Process.Start(new ProcessStartInfo {
                    FileName = HeartRateSettings.GetFilename(),
                    UseShellExecute = true,
                    Verb = "EDIT"
                }))
                {
                    process.WaitForExit();
                }

                lock (_updateSync)
                {
                    LoadSettingsLocked();
                }
            }) {
                IsBackground = true,
                Name = "Edit config"
            };

            thread.Start();
        }

        private void LoadSettingsLocked()
        {
            _settings.Load();

            _log = new LogFile(_settings, GetFilename(_settings.LogFile));
            _ibi = new IBIFile(GetFilename(_settings.IBIFile));
        }

        private string GetFilename(string inputFilename)
        {
            return string.IsNullOrWhiteSpace(inputFilename)
                ? null
                : DateTimeFormatter.FormatStringTokens(
                    inputFilename, _startedAt, forFilepath: true);
        }

        private void uxExitMenuItem_Click(object sender, EventArgs e)
        {
            Environment.Exit(0);
        }

        private bool TryPromptColor(Color current, out Color color)
        {
            color = default;

            using (var dlg = new ColorDialog())
            {
                dlg.Color = current;

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    color = dlg.Color;
                    return true;
                }
            }

            return false;
        }

        private bool TryPromptFont(string current, out string font)
        {
            font = default;

            using (var dlg = new FontDialog()
            {
                FontMustExist = true
            })
            {
                using (dlg.Font = new Font(current, 10, GraphicsUnit.Pixel))
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        font = dlg.Font.Name;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryPromptFile(string current, string filter, out string file)
        {
            file = default;

            using (var dlg = new OpenFileDialog
            {
                CheckFileExists = true,
                FileName = current,
                Filter = filter
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    file = dlg.FileName;
                    return true;
                }
            }

            return false;
        }

        private void updateSettingColor(ref Color settingColor)
        {
            if (!TryPromptColor(settingColor, out var color)) return;

            lock (_updateSync)
            {
                settingColor = color;
                _settings.Save();
            }

            UpdateUI();
        }

        private void editFontColorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            updateSettingColor(ref _settings.Color);
        }

        private void editIconFontWarningColorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            updateSettingColor(ref _settings.WarnColor);
        }

        private void editWindowFontColorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            updateSettingColor(ref _settings.UIColor);
        }

        private void editWindowFontWarningColorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            updateSettingColor(ref _settings.UIWarnColor);
        }

        private void selectIconFontToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryPromptFont(_settings.FontName, out var font)) return;

            lock (_updateSync)
            {
                _settings.FontName = font;
                _settings.Save();
            }

            UpdateUI();
        }

        private void selectWindowFontToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryPromptFont(_settings.UIFontName, out var font)) return;

            lock (_updateSync)
            {
                _settings.UIFontName = font;
                _settings.Save();
            }

            UpdateUI();
        }

        private void selectBackgroundImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryPromptFile(_settings.UIBackgroundFile, "Image files|*.bmp;*.gif;*.jpeg;*.png;*.tiff|All files (*.*)|*.*", out var file)) return;

            lock (_updateSync)
            {
                _settings.UIBackgroundFile = file;
                _settings.Save();
            }

            UpdateUI();
        }

        private void removeBackgroundImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            lock (_updateSync)
            {
                _settings.UIBackgroundFile = null;
                _settings.Save();
            }

            UpdateUI();
        }

        private void selectBackgroundLayoutToolStripMenuItem_SelectedIndexChanged(object sender, EventArgs e)
        {
            var text = selectBackgroundLayoutToolStripMenuItem.Text;

            if (!Enum.TryParse<ImageLayout>(text, true, out var layout)) return;

            lock (_updateSync)
            {
                _settings.UIBackgroundLayout = layout;
                _settings.Save();
            }

            UpdateUI();
        }
    }
}
