using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexUsageWidget
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                return SelfTests.Run(args.Length > 1 ? args[1] : null);

            if (args.Length > 1 && string.Equals(args[0], "--screenshot", StringComparison.OrdinalIgnoreCase))
                return ScreenshotRenderer.Render(args[1]);

            if (args.Length > 1 && string.Equals(args[0], "--diagnostic", StringComparison.OrdinalIgnoreCase))
                return Diagnostics.Run(args[1]);

            bool created;
            using (var mutex = new Mutex(true, "Local\\CodexUsageWidget.SingleInstance", out created))
            {
                if (!created)
                    return 0;

                Application.Run(new UsageWidgetForm(false));
            }

            return 0;
        }
    }

    internal sealed class UsageSnapshot
    {
        public DateTime TimestampUtc;
        public double UsedPercent;
        public DateTime ResetAtUtc;
        public double? Credits;
        public string PlanType;

        public double RemainingPercent
        {
            get { return Math.Max(0.0, Math.Min(100.0, 100.0 - UsedPercent)); }
        }
    }

    internal sealed class UsageSample
    {
        public DateTime TimestampUtc;
        public double UsedPercent;
        public double? Credits;
    }

    internal sealed class BurnMetrics
    {
        public string Level;
        public string DisplayRate;
        public string Trend;
        public Color Color;
        public bool CreditMode;
        public double RecentDraw;
        public double PeakRate;
    }

    internal static class UsageIntensity
    {
        public static readonly Color Idle = Color.FromArgb(132, 142, 158);
        public static readonly Color Light = Color.FromArgb(75, 210, 150);
        public static readonly Color Moderate = Color.FromArgb(244, 184, 71);
        public static readonly Color Heavy = Color.FromArgb(255, 102, 82);
        public static readonly Color VeryHigh = Color.FromArgb(194, 90, 255);

        public static string LevelFor(double value, bool creditMode)
        {
            if (value <= 0.0001) return "IDLE";
            if (creditMode)
            {
                if (value < 15.0) return "LIGHT";
                if (value < 60.0) return "MODERATE";
                if (value < 150.0) return "HEAVY";
                return "VERY HIGH";
            }

            if (value < 0.5) return "LIGHT";
            if (value < 2.0) return "MODERATE";
            if (value < 5.0) return "HEAVY";
            return "VERY HIGH";
        }

        public static Color ColorFor(double value, bool creditMode)
        {
            return ColorForLevel(LevelFor(value, creditMode));
        }

        public static Color ColorForLevel(string level)
        {
            if (string.Equals(level, "LIGHT", StringComparison.OrdinalIgnoreCase)) return Light;
            if (string.Equals(level, "MODERATE", StringComparison.OrdinalIgnoreCase)) return Moderate;
            if (string.Equals(level, "HEAVY", StringComparison.OrdinalIgnoreCase)) return Heavy;
            if (string.Equals(level, "VERY HIGH", StringComparison.OrdinalIgnoreCase)) return VeryHigh;
            return Idle;
        }
    }

    internal static class BurnCalculator
    {
        private static readonly Color Learning = Color.FromArgb(154, 132, 255);
        private const double IdleAfterSeconds = 90.0;
        private const double MaxComparableGapSeconds = 45.0;

        public static BurnMetrics Calculate(IList<UsageSample> source, DateTime nowUtc)
        {
            var samples = source
                .Where(s => s.TimestampUtc >= nowUtc.AddDays(-7))
                .OrderBy(s => s.TimestampUtc)
                .ToList();

            if (samples.Count < 2)
                return NewLearning();

            bool creditsAvailable = samples[samples.Count - 1].Credits.HasValue;
            bool creditChanged = false;
            for (int i = 1; i < samples.Count; i++)
            {
                if (samples[i - 1].Credits.HasValue && samples[i].Credits.HasValue &&
                    samples[i - 1].Credits.Value - samples[i].Credits.Value > 0.5 &&
                    IsComparableGap(samples[i - 1], samples[i]))
                {
                    creditChanged = true;
                    break;
                }
            }

            bool allowanceExhausted = samples[samples.Count - 1].UsedPercent >= 99.999;
            bool creditMode = creditsAvailable && (creditChanged || allowanceExhausted);
            DateTime? lastChangeUtc = null;
            double latestBurstDraw = 0;
            double latestBurstPeakRate = 0;

            for (int i = 1; i < samples.Count; i++)
            {
                double draw = 0;
                if (creditMode && samples[i - 1].Credits.HasValue && samples[i].Credits.HasValue)
                    draw = samples[i - 1].Credits.Value - samples[i].Credits.Value;
                else
                    draw = samples[i].UsedPercent - samples[i - 1].UsedPercent;

                double epsilon = creditMode ? 0.5 : 0.05;
                if (draw <= epsilon) continue;

                double sampleGapSeconds = (samples[i].TimestampUtc - samples[i - 1].TimestampUtc).TotalSeconds;
                if (!IsComparableGap(samples[i - 1], samples[i]))
                    continue;

                if (!lastChangeUtc.HasValue ||
                    (samples[i].TimestampUtc - lastChangeUtc.Value).TotalSeconds >= IdleAfterSeconds)
                {
                    latestBurstDraw = 0;
                    latestBurstPeakRate = 0;
                }

                latestBurstDraw += draw;
                latestBurstPeakRate = Math.Max(latestBurstPeakRate, draw * 60.0 / sampleGapSeconds);
                lastChangeUtc = samples[i].TimestampUtc;
            }

            double observedSeconds = (samples[samples.Count - 1].TimestampUtc - samples[0].TimestampUtc).TotalSeconds;
            if (!lastChangeUtc.HasValue)
            {
                if (observedSeconds < IdleAfterSeconds)
                    return NewLearning();
                return NewMetric("IDLE", 0, 0, "No recent draw", "", UsageIntensity.Idle, creditMode);
            }

            TimeSpan sinceChange = nowUtc - lastChangeUtc.Value;
            if (sinceChange.TotalSeconds >= IdleAfterSeconds)
                return NewMetric("IDLE", latestBurstDraw, latestBurstPeakRate,
                    FormatRecentDraw(latestBurstDraw, creditMode) + "  •  " + FormatElapsed(sinceChange),
                    "", UsageIntensity.Idle, creditMode);

            string level = UsageIntensity.LevelFor(latestBurstDraw, creditMode);
            return NewMetric(level, latestBurstDraw, latestBurstPeakRate,
                FormatRecentDraw(latestBurstDraw, creditMode) + " so far", "",
                UsageIntensity.ColorForLevel(level), creditMode);
        }

        private static bool IsComparableGap(UsageSample previous, UsageSample current)
        {
            double seconds = (current.TimestampUtc - previous.TimestampUtc).TotalSeconds;
            return seconds > 0 && seconds <= MaxComparableGapSeconds;
        }

        private static BurnMetrics NewLearning()
        {
            return NewMetric("LEARNING", 0, 0, "Collecting samples", "•", Learning, true);
        }

        private static BurnMetrics NewMetric(string level, double recentDraw, double peakRate, string display, string trend, Color color, bool creditMode)
        {
            return new BurnMetrics
            {
                Level = level,
                RecentDraw = recentDraw,
                PeakRate = peakRate,
                DisplayRate = display,
                Trend = trend,
                Color = color,
                CreditMode = creditMode
            };
        }

        private static string FormatRecentDraw(double draw, bool creditMode)
        {
            string number = draw < 10 ? draw.ToString("0.0", CultureInfo.InvariantCulture) : draw.ToString("0", CultureInfo.InvariantCulture);
            if (!creditMode) return number + "% allowance";
            return number + " credit" + (Math.Abs(draw - 1.0) < 0.01 ? "" : "s");
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalDays >= 1)
                return (int)elapsed.TotalDays + "d " + elapsed.Hours + "h";
            if (elapsed.TotalHours >= 1)
                return (int)elapsed.TotalHours + "h " + elapsed.Minutes + "m";
            return Math.Max(1, (int)elapsed.TotalMinutes) + "m";
        }
    }

    internal static class ActivitySeriesCalculator
    {
        private const double MaxComparableGapSeconds = 45.0;

        public static List<double> Build(IList<UsageSample> source)
        {
            var values = new List<double>();
            if (source == null || source.Count == 0) return values;

            bool creditMode = IsCreditMode(source);
            values.Add(0);
            for (int i = 1; i < source.Count; i++)
            {
                double gap = (source[i].TimestampUtc - source[i - 1].TimestampUtc).TotalSeconds;
                if (gap <= 0 || gap > MaxComparableGapSeconds)
                {
                    values.Add(0);
                    continue;
                }

                double draw = creditMode && source[i - 1].Credits.HasValue && source[i].Credits.HasValue
                    ? source[i - 1].Credits.Value - source[i].Credits.Value
                    : source[i].UsedPercent - source[i - 1].UsedPercent;
                double epsilon = creditMode ? 0.5 : 0.05;
                values.Add(draw > epsilon ? draw * 60.0 / gap : 0);
            }
            return values;
        }

        public static bool IsCreditMode(IList<UsageSample> source)
        {
            if (source == null || source.Count == 0) return false;
            bool creditsAvailable = source[source.Count - 1].Credits.HasValue;
            bool creditChanged = false;
            for (int i = 1; i < source.Count; i++)
            {
                double gap = (source[i].TimestampUtc - source[i - 1].TimestampUtc).TotalSeconds;
                if (gap > 0 && gap <= MaxComparableGapSeconds &&
                    source[i - 1].Credits.HasValue && source[i].Credits.HasValue &&
                    source[i - 1].Credits.Value - source[i].Credits.Value > 0.5)
                {
                    creditChanged = true;
                    break;
                }
            }
            return creditsAvailable && (creditChanged || source[source.Count - 1].UsedPercent >= 99.999);
        }
    }

    internal static class UsageFormatting
    {
        public static string CreditBalance(double value)
        {
            return Math.Ceiling(value).ToString("N0", CultureInfo.CurrentCulture);
        }

        public static string PeakRate(double value, bool creditMode)
        {
            if (value <= 0.0001) return "—";
            string number = value < 10
                ? value.ToString("0.0", CultureInfo.InvariantCulture)
                : value.ToString("0", CultureInfo.InvariantCulture);
            return creditMode ? number + " credits/min" : number + "%/min";
        }
    }

    internal sealed class AdaptiveRefreshCadence
    {
        public const int IdleIntervalMs = 15000;
        public const int ActiveIntervalMs = 3000;
        public const double ActiveHoldSeconds = 90.0;
        private DateTime _activeUntilUtc = DateTime.MinValue;

        public int GetIntervalMs(DateTime nowUtc)
        {
            return nowUtc < _activeUntilUtc ? ActiveIntervalMs : IdleIntervalMs;
        }

        public void NotifyUsage(DateTime nowUtc)
        {
            DateTime next = nowUtc.AddSeconds(ActiveHoldSeconds);
            if (next > _activeUntilUtc)
                _activeUntilUtc = next;
        }

        public bool Observe(UsageSnapshot previous, UsageSnapshot current, DateTime nowUtc)
        {
            if (!IsMeaningfulUsage(previous, current)) return false;
            NotifyUsage(nowUtc);
            return true;
        }

        public static bool IsMeaningfulUsage(UsageSnapshot previous, UsageSnapshot current)
        {
            if (previous == null || current == null) return false;

            bool allowanceDraw = current.UsedPercent - previous.UsedPercent > 0.05;
            bool creditDraw = previous.Credits.HasValue && current.Credits.HasValue &&
                              previous.Credits.Value - current.Credits.Value > 0.5;
            return allowanceDraw || creditDraw;
        }
    }

    internal static class RateLimitParser
    {
        public static UsageSnapshot ParseResult(object resultObject)
        {
            var result = AsDictionary(resultObject);
            if (result == null) return null;

            Dictionary<string, object> bucket = null;
            var byId = GetDictionary(result, "rateLimitsByLimitId");
            if (byId != null)
                bucket = GetDictionary(byId, "codex");
            if (bucket == null)
                bucket = GetDictionary(result, "rateLimits");
            if (bucket == null) return null;

            var primary = GetDictionary(bucket, "primary");
            if (primary == null) return null;

            double used = GetDouble(primary, "usedPercent", double.NaN);
            double reset = GetDouble(primary, "resetsAt", 0);
            if (double.IsNaN(used)) return null;

            double? credits = null;
            var creditData = GetDictionary(bucket, "credits") ?? GetDictionary(result, "credits");
            if (creditData != null)
            {
                object raw;
                if (creditData.TryGetValue("balance", out raw) && raw != null)
                {
                    double parsed;
                    if (double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        credits = parsed;
                }
            }

            string plan = GetString(bucket, "planType") ?? GetString(result, "planType");
            return new UsageSnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                UsedPercent = used,
                ResetAtUtc = reset > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(reset) : DateTime.MinValue,
                Credits = credits,
                PlanType = plan
            };
        }

        public static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            return source.TryGetValue(key, out value) ? AsDictionary(value) : null;
        }

        public static string GetString(Dictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            return source.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        public static double GetDouble(Dictionary<string, object> source, string key, double fallback)
        {
            if (source == null) return fallback;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
    }

    internal sealed class CodexUsageClient : IDisposable
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly object _writeLock = new object();
        private Process _process;
        private int _nextId = 10;
        private int _rateRequestId;
        private bool _initialized;
        private bool _disposed;
        private string _lastError;

        public event Action<UsageSnapshot> SnapshotReceived;
        public event Action<string> StatusChanged;
        public event Action RateLimitsChanged;

        public bool IsRunning
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch { return false; }
            }
        }

        public void Start()
        {
            if (_disposed || IsRunning) return;

            string executable = FindCodexExecutable();
            if (string.IsNullOrEmpty(executable))
            {
                RaiseStatus("Codex command not found");
                return;
            }

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "app-server --stdio",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                _process = new Process { StartInfo = start, EnableRaisingEvents = true };
                _process.OutputDataReceived += OnOutput;
                _process.ErrorDataReceived += OnError;
                _process.Exited += OnExited;
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                _initialized = false;
                RaiseStatus("Connecting…");

                Send(new Dictionary<string, object>
                {
                    { "method", "initialize" },
                    { "id", 1 },
                    { "params", new Dictionary<string, object>
                        {
                            { "clientInfo", new Dictionary<string, object>
                                {
                                    { "name", "codex_usage_widget" },
                                    { "title", "Codex Usage Widget" },
                                    { "version", "1.0.0" }
                                }
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                RaiseStatus("Unable to start Codex: " + ex.Message);
                SafeStop();
            }
        }

        public void Restart()
        {
            SafeStop();
            if (!_disposed) Start();
        }

        public void RequestRateLimits()
        {
            if (!_initialized || !IsRunning) return;
            int id = Interlocked.Increment(ref _nextId);
            _rateRequestId = id;
            Send(new Dictionary<string, object>
            {
                { "method", "account/rateLimits/read" },
                { "id", id }
            });
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                var message = _json.DeserializeObject(e.Data) as Dictionary<string, object>;
                if (message == null) return;

                object idValue;
                int id = -1;
                if (message.TryGetValue("id", out idValue) && idValue != null)
                {
                    try { id = Convert.ToInt32(idValue, CultureInfo.InvariantCulture); } catch { }
                }

                if (id == 1 && message.ContainsKey("result"))
                {
                    _initialized = true;
                    Send(new Dictionary<string, object>
                    {
                        { "method", "initialized" },
                        { "params", new Dictionary<string, object>() }
                    });
                    RaiseStatus("Connected");
                    RequestRateLimits();
                    return;
                }

                if (id == _rateRequestId)
                {
                    object errorObject;
                    if (message.TryGetValue("error", out errorObject))
                    {
                        var error = RateLimitParser.AsDictionary(errorObject);
                        string text = RateLimitParser.GetString(error, "message") ?? "Unable to read usage";
                        RaiseStatus(text.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "Open Codex and sign in"
                            : text);
                        return;
                    }

                    object result;
                    if (message.TryGetValue("result", out result))
                    {
                        var snapshot = RateLimitParser.ParseResult(result);
                        if (snapshot != null)
                        {
                            RaiseStatus("Live");
                            var handler = SnapshotReceived;
                            if (handler != null) handler(snapshot);
                        }
                    }
                    return;
                }

                string method = RateLimitParser.GetString(message, "method");
                if (string.Equals(method, "account/rateLimits/updated", StringComparison.OrdinalIgnoreCase))
                {
                    var changed = RateLimitsChanged;
                    if (changed != null) changed();
                    RequestRateLimits();
                }
                else if (string.Equals(method, "account/updated", StringComparison.OrdinalIgnoreCase))
                    RequestRateLimits();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
        }

        private void OnError(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _lastError = e.Data;
        }

        private void OnExited(object sender, EventArgs e)
        {
            _initialized = false;
            if (!_disposed)
                RaiseStatus(string.IsNullOrEmpty(_lastError) ? "Disconnected — retrying" : "Disconnected — retrying");
        }

        private void Send(Dictionary<string, object> message)
        {
            try
            {
                string line = _json.Serialize(message);
                lock (_writeLock)
                {
                    if (!IsRunning) return;
                    _process.StandardInput.WriteLine(line);
                    _process.StandardInput.Flush();
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                RaiseStatus("Connection interrupted");
            }
        }

        private void RaiseStatus(string status)
        {
            var handler = StatusChanged;
            if (handler != null) handler(status);
        }

        private static string FindCodexExecutable()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_WIDGET_CODEX_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string root = Path.Combine(local, "OpenAI", "Codex", "bin");
                if (Directory.Exists(root))
                {
                    var matches = Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .ToArray();
                    if (matches.Length > 0) return matches[0];
                }
            }
            catch { }

            return "codex.exe";
        }

        private void SafeStop()
        {
            var process = _process;
            _process = null;
            _initialized = false;
            if (process == null) return;

            try { process.CancelOutputRead(); } catch { }
            try { process.CancelErrorRead(); } catch { }
            try { process.StandardInput.Close(); } catch { }
            try
            {
                if (!process.HasExited && !process.WaitForExit(750))
                    process.Kill();
            }
            catch { }
            try { process.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            SafeStop();
        }
    }

    internal sealed class HistoryStore
    {
        private readonly string _folder;
        private readonly string _historyPath;
        private readonly string _settingsPath;
        private readonly UsageCsvLogger _csvLogger;
        private readonly object _sync = new object();
        private readonly List<UsageSample> _samples = new List<UsageSample>();

        public HistoryStore()
        {
            _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget");
            _historyPath = Path.Combine(_folder, "history.tsv");
            _settingsPath = Path.Combine(_folder, "settings.ini");
            _csvLogger = new UsageCsvLogger(UsageCsvLogger.GetDefaultPath());
            LoadHistory();
        }

        public List<UsageSample> Samples
        {
            get { lock (_sync) return new List<UsageSample>(_samples); }
        }

        public void Add(UsageSnapshot snapshot)
        {
            var sample = new UsageSample
            {
                TimestampUtc = snapshot.TimestampUtc,
                UsedPercent = snapshot.UsedPercent,
                Credits = snapshot.Credits
            };

            UsageSample previous;

            lock (_sync)
            {
                previous = _samples.Count > 0 ? _samples[_samples.Count - 1] : null;
                _samples.Add(sample);
                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                _samples.RemoveAll(s => s.TimestampUtc < cutoff);
            }

            _csvLogger.Append(previous, sample);

            try
            {
                Directory.CreateDirectory(_folder);
                string line = sample.TimestampUtc.ToString("o", CultureInfo.InvariantCulture) + "\t" +
                              sample.UsedPercent.ToString("0.####", CultureInfo.InvariantCulture) + "\t" +
                              (sample.Credits.HasValue ? sample.Credits.Value.ToString("0.########", CultureInfo.InvariantCulture) : "") + Environment.NewLine;
                File.AppendAllText(_historyPath, line, Encoding.UTF8);
                if (_samples.Count % 240 == 0) RewriteHistory();
            }
            catch { }
        }

        public Point? LoadPosition()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return null;
                var values = File.ReadAllLines(_settingsPath)
                    .Select(line => line.Split(new[] { '=' }, 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
                if (!values.ContainsKey("layout") || !string.Equals(values["layout"], "vertical-v1", StringComparison.OrdinalIgnoreCase))
                    return null;
                int x, y;
                if (values.ContainsKey("x") && values.ContainsKey("y") &&
                    int.TryParse(values["x"], out x) && int.TryParse(values["y"], out y))
                    return new Point(x, y);
            }
            catch { }
            return null;
        }

        public void SavePosition(Point point)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                File.WriteAllText(_settingsPath, "layout=vertical-v1" + Environment.NewLine +
                    "x=" + point.X + Environment.NewLine + "y=" + point.Y + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyPath)) return;
                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (string line in File.ReadAllLines(_historyPath))
                {
                    string[] parts = line.Split('\t');
                    DateTime at;
                    double used;
                    if (parts.Length < 2 || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) ||
                        !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out used))
                        continue;
                    if (at < cutoff) continue;
                    double credit;
                    double? credits = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out credit)
                        ? (double?)credit
                        : null;
                    _samples.Add(new UsageSample { TimestampUtc = at.ToUniversalTime(), UsedPercent = used, Credits = credits });
                }
            }
            catch { }
        }

        private void RewriteHistory()
        {
            try
            {
                var builder = new StringBuilder();
                foreach (var sample in _samples)
                {
                    builder.Append(sample.TimestampUtc.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(sample.UsedPercent.ToString("0.####", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(sample.Credits.HasValue ? sample.Credits.Value.ToString("0.########", CultureInfo.InvariantCulture) : "")
                        .AppendLine();
                }
                File.WriteAllText(_historyPath, builder.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    internal sealed class UsageCsvLogger
    {
        private const double MaxComparableGapSeconds = 45.0;
        private const string Header = "TimestampLocal,TimestampUTC,CreditBalance,CreditBalanceChange,CreditsUsedSincePrevious,SecondsSincePrevious,CreditsPerMinute,WeeklyUsedPercent,WeeklyRemainingPercent";
        private readonly string _path;
        private readonly object _sync = new object();

        public UsageCsvLogger(string path)
        {
            _path = path;
        }

        public static string GetDefaultPath()
        {
            string executableFolder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folder = new DirectoryInfo(executableFolder);
            string outputFolder = string.Equals(folder.Name, "dist", StringComparison.OrdinalIgnoreCase) && folder.Parent != null
                ? folder.Parent.FullName
                : executableFolder;
            return Path.Combine(outputFolder, "usage-log.csv");
        }

        public void Append(UsageSample previous, UsageSample current)
        {
            if (current == null) return;

            try
            {
                lock (_sync)
                {
                    string folder = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                    bool newLog = !File.Exists(_path) || new FileInfo(_path).Length == 0;
                    if (newLog)
                        File.WriteAllText(_path, Header + Environment.NewLine, new UTF8Encoding(true));

                    File.AppendAllText(_path, BuildRow(newLog ? null : previous, current) + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never interrupt the widget. A later sample will try again.
            }
        }

        public static string BuildRow(UsageSample previous, UsageSample current)
        {
            double? elapsedSeconds = null;
            double? balanceChange = null;
            double? creditsUsed = null;
            double? creditsPerMinute = null;

            if (previous != null)
            {
                double elapsed = (current.TimestampUtc - previous.TimestampUtc).TotalSeconds;
                if (elapsed > 0)
                {
                    elapsedSeconds = elapsed;
                    bool comparable = elapsed <= MaxComparableGapSeconds && previous.Credits.HasValue && current.Credits.HasValue;
                    if (comparable)
                    {
                        balanceChange = current.Credits.Value - previous.Credits.Value;
                        creditsUsed = Math.Max(0.0, previous.Credits.Value - current.Credits.Value);
                        creditsPerMinute = creditsUsed.Value * 60.0 / elapsed;
                    }
                }
            }

            DateTimeOffset local = new DateTimeOffset(current.TimestampUtc).ToLocalTime();
            return string.Join(",", new[]
            {
                local.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
                current.TimestampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                FormatNumber(current.Credits),
                FormatNumber(balanceChange),
                FormatNumber(creditsUsed),
                FormatNumber(elapsedSeconds),
                FormatNumber(creditsPerMinute),
                current.UsedPercent.ToString("0.####", CultureInfo.InvariantCulture),
                Math.Max(0.0, Math.Min(100.0, 100.0 - current.UsedPercent)).ToString("0.####", CultureInfo.InvariantCulture)
            });
        }

        private static string FormatNumber(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.########", CultureInfo.InvariantCulture) : "";
        }
    }

    internal sealed class SparklineControl : Control
    {
        private List<UsageSample> _samples = new List<UsageSample>();
        public float VisualScale { get; set; }

        public SparklineControl()
        {
            VisualScale = 1.0f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void SetData(List<UsageSample> samples)
        {
            _samples = samples ?? new List<UsageSample>();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var plot = new RectangleF(ScaleValue(1), ScaleValue(24), Math.Max(1, Width - ScaleValue(3)), Math.Max(1, Height - ScaleValue(42)));

            using (var labelBrush = new SolidBrush(Color.FromArgb(112, 122, 140)))
            using (var labelFont = new Font("Segoe UI", 10.5f * VisualScale, FontStyle.Bold, GraphicsUnit.Pixel))
                e.Graphics.DrawString("LAST 60 MIN", labelFont, labelBrush, 0, 0);

            DrawLegend(e.Graphics);

            using (var gridPen = new Pen(Color.FromArgb(43, 47, 57), Math.Max(1, VisualScale)))
            {
                e.Graphics.DrawLine(gridPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
                e.Graphics.DrawLine(gridPen, plot.Left, plot.Top + plot.Height / 2, plot.Right, plot.Top + plot.Height / 2);
            }

            DateTime now = DateTime.UtcNow;
            var recent = _samples.Where(s => s.TimestampUtc >= now.AddMinutes(-60)).OrderBy(s => s.TimestampUtc).ToList();
            if (recent.Count < 2)
            {
                using (var pen = new Pen(Color.FromArgb(65, 72, 86), 1.5f * VisualScale))
                    e.Graphics.DrawLine(pen, plot.Left, plot.Top + plot.Height / 2, plot.Right, plot.Top + plot.Height / 2);
                return;
            }

            var values = ActivitySeriesCalculator.Build(recent);
            bool creditMode = ActivitySeriesCalculator.IsCreditMode(recent);
            double min = 0;
            double max = values.Max();
            bool hasActivity = max > 0.0001;
            if (!hasActivity) max = 1;
            else max *= 1.08;

            var points = new List<PointF>();
            for (int i = 0; i < recent.Count; i++)
            {
                double ageMinutes = (recent[i].TimestampUtc - now.AddMinutes(-60)).TotalMinutes;
                float x = plot.Left + (float)(Math.Max(0, Math.Min(60, ageMinutes)) / 60.0) * plot.Width;
                float y = plot.Bottom - (float)((values[i] - min) / (max - min)) * plot.Height;
                points.Add(new PointF(x, y));
            }

            if (points.Count >= 2)
            {
                for (int i = 1; i < points.Count; i++)
                {
                    double segmentActivity = Math.Max(values[i - 1], values[i]);
                    Color segmentColor = UsageIntensity.ColorFor(segmentActivity, creditMode);
                    if (segmentActivity > 0.0001)
                    {
                        PointF[] area =
                        {
                            new PointF(points[i - 1].X, plot.Bottom),
                            points[i - 1],
                            points[i],
                            new PointF(points[i].X, plot.Bottom)
                        };
                        using (var fill = new SolidBrush(Color.FromArgb(42, segmentColor)))
                            e.Graphics.FillPolygon(fill, area);
                    }
                    using (var pen = new Pen(segmentColor, 2.0f * VisualScale))
                        e.Graphics.DrawLine(pen, points[i - 1], points[i]);
                }
            }
        }

        private void DrawLegend(Graphics graphics)
        {
            string[] labels = { "IDLE", "LIGHT", "MOD", "HIGH", "V.HIGH" };
            Color[] colors = { UsageIntensity.Idle, UsageIntensity.Light, UsageIntensity.Moderate, UsageIntensity.Heavy, UsageIntensity.VeryHigh };
            float sectionWidth = Width / 5.0f;
            float y = Height - ScaleValue(12);
            using (var font = new Font("Segoe UI", 6.5f * VisualScale, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    float x = i * sectionWidth;
                    using (var brush = new SolidBrush(colors[i]))
                    {
                        graphics.FillEllipse(brush, x, y + ScaleValue(2), ScaleValue(3), ScaleValue(3));
                        graphics.DrawString(labels[i], font, brush, x + ScaleValue(4), y);
                    }
                }
            }
        }

        private float ScaleValue(float value)
        {
            return value * VisualScale;
        }
    }

    internal sealed class UsageWidgetForm : Form
    {
        private const float DisplayScale = 2.0f;
        private readonly Color _background = Color.FromArgb(24, 24, 24);
        private readonly Color _text = Color.FromArgb(244, 246, 250);
        private readonly Color _muted = Color.FromArgb(142, 150, 166);
        private readonly Color _purple = Color.FromArgb(161, 132, 255);
        private readonly Color _green = Color.FromArgb(75, 210, 150);
        private readonly bool _demoMode;
        private readonly HistoryStore _history;
        private readonly AdaptiveRefreshCadence _refreshCadence = new AdaptiveRefreshCadence();
        private CodexUsageClient _client;
        private UsageSnapshot _lastSnapshot;
        private System.Windows.Forms.Timer _timer;
        private Label _status;
        private Label _weeklyValue;
        private Label _resetValue;
        private Label _creditValue;
        private Label _creditCaption;
        private Label _burnBadge;
        private Label _burnValue;
        private Label _peakValue;
        private Label _updated;
        private Panel _progressTrack;
        private Panel _progressFill;
        private SparklineControl _sparkline;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _topMostMenu;
        private ToolStripMenuItem _startupMenu;
        private bool _suppressStartupToggle;
        private Point _dragOrigin;
        private bool _dragging;

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int DestroyIcon(IntPtr handle);

        public UsageWidgetForm(bool demoMode)
        {
            _demoMode = demoMode;
            _history = new HistoryStore();
            BuildUi();
            ScaleInterface(DisplayScale);
            RestorePosition();
            BuildMenuAndTray();
            WireDragging(this);

            if (_demoMode)
            {
                LoadDemoData();
            }
            else
            {
                _client = new CodexUsageClient();
                _client.SnapshotReceived += snapshot => SafeUi(() => ApplySnapshot(snapshot));
                _client.StatusChanged += status => SafeUi(() => SetStatus(status));
                _client.RateLimitsChanged += () => SafeUi(() => EnterActiveRefresh());
                _client.Start();
            }

            _timer = new System.Windows.Forms.Timer { Interval = _refreshCadence.GetIntervalMs(DateTime.UtcNow) };
            _timer.Tick += delegate
            {
                UpdateCountdown();
                ApplyRefreshInterval();
                if (_demoMode) return;
                if (_client != null && _client.IsRunning) _client.RequestRateLimits();
                else if (_client != null) _client.Restart();
            };
            _timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        private void BuildUi()
        {
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(222, 500);
            BackColor = _background;
            ForeColor = _text;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 1.0;

            var title = NewLabel("CODEX USAGE", 18, 13, 120, 20, 9.5f, FontStyle.Bold, _muted);
            title.Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            Controls.Add(title);

            _status = NewLabel("● CONNECTING", 132, 13, 64, 20, 9.5f, FontStyle.Bold, _muted);
            _status.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_status);

            var hide = NewLabel("×", 198, 5, 20, 27, 21f, FontStyle.Regular, _muted);
            hide.TextAlign = ContentAlignment.MiddleCenter;
            hide.AutoEllipsis = false;
            hide.Cursor = Cursors.Hand;
            hide.Click += delegate { Hide(); };
            var hideTip = new ToolTip();
            hideTip.SetToolTip(hide, "Hide to tray");
            Controls.Add(hide);

            Controls.Add(NewLabel("WEEKLY", 18, 43, 100, 18, 10.5f, FontStyle.Bold, _muted));
            _weeklyValue = NewLabel("—", 18, 57, 186, 38, 32f, FontStyle.Bold, _text);
            Controls.Add(_weeklyValue);

            _progressTrack = new Panel { Location = new Point(18, 96), Size = new Size(186, 6), BackColor = Color.FromArgb(48, 52, 62) };
            _progressFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 6), BackColor = _green };
            _progressTrack.Controls.Add(_progressFill);
            Controls.Add(_progressTrack);

            _resetValue = NewLabel("Waiting for usage data", 18, 107, 186, 20, 11.5f, FontStyle.Regular, _muted);
            Controls.Add(_resetValue);

            Controls.Add(new Panel { Location = new Point(18, 136), Size = new Size(186, 1), BackColor = Color.FromArgb(48, 52, 62) });

            Controls.Add(NewLabel("CREDITS", 18, 147, 100, 18, 10.5f, FontStyle.Bold, _muted));
            _creditValue = NewLabel("—", 18, 161, 186, 38, 32f, FontStyle.Bold, _text);
            Controls.Add(_creditValue);
            _creditCaption = NewLabel("Available balance", 18, 202, 186, 20, 11.5f, FontStyle.Regular, _muted);
            Controls.Add(_creditCaption);

            Controls.Add(new Panel { Location = new Point(18, 231), Size = new Size(186, 1), BackColor = Color.FromArgb(48, 52, 62) });

            Controls.Add(NewLabel("RECENT DRAW", 18, 243, 110, 17, 10.5f, FontStyle.Bold, _muted));
            _burnBadge = NewLabel("LEARNING", 18, 263, 92, 24, 10.5f, FontStyle.Bold, _purple);
            _burnBadge.TextAlign = ContentAlignment.MiddleCenter;
            _burnBadge.BackColor = Color.FromArgb(42, 38, 60);
            Controls.Add(_burnBadge);
            _burnValue = NewLabel("Collecting samples", 18, 291, 186, 24, 12f, FontStyle.Regular, _text);
            _burnValue.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_burnValue);

            Controls.Add(NewLabel("PEAK RATE", 18, 318, 66, 18, 9f, FontStyle.Bold, _muted));
            _peakValue = NewLabel("—", 88, 315, 116, 22, 11f, FontStyle.Regular, _text);
            _peakValue.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_peakValue);

            Controls.Add(new Panel { Location = new Point(18, 347), Size = new Size(186, 1), BackColor = Color.FromArgb(48, 52, 62) });

            _sparkline = new SparklineControl { Location = new Point(18, 356), Size = new Size(186, 102) };
            Controls.Add(_sparkline);

            _updated = NewLabel("Starting…", 18, 464, 186, 29, 9.5f, FontStyle.Regular, Color.FromArgb(103, 112, 129));
            _updated.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_updated);

            foreach (Control control in Controls)
                WireDragging(control);
        }

        private void BuildMenuAndTray()
        {
            _menu = new ContextMenuStrip();
            _menu.BackColor = Color.FromArgb(30, 30, 30);
            _menu.ForeColor = _text;
            _menu.RenderMode = ToolStripRenderMode.System;

            var show = new ToolStripMenuItem("Show widget");
            show.Click += delegate { ShowWidget(); };
            var refresh = new ToolStripMenuItem("Refresh now");
            refresh.Click += delegate { if (_client != null) _client.RequestRateLimits(); };
            _topMostMenu = new ToolStripMenuItem("Always on top") { Checked = true, CheckOnClick = true };
            _topMostMenu.CheckedChanged += delegate { TopMost = _topMostMenu.Checked; };
            _startupMenu = new ToolStripMenuItem("Launch with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
            _startupMenu.CheckedChanged += delegate
            {
                if (_suppressStartupToggle) return;
                bool wanted = _startupMenu.Checked;
                if (!SetStartupEnabled(wanted))
                {
                    _suppressStartupToggle = true;
                    _startupMenu.Checked = !wanted;
                    _suppressStartupToggle = false;
                }
            };
            var openUsage = new ToolStripMenuItem("Open Codex usage page");
            openUsage.Click += delegate { try { Process.Start("https://chatgpt.com/codex/settings/usage"); } catch { } };
            var resetPosition = new ToolStripMenuItem("Reset position");
            resetPosition.Click += delegate { PositionAtDefault(); };
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { Close(); };

            _menu.Items.Add(show);
            _menu.Items.Add(refresh);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_topMostMenu);
            _menu.Items.Add(_startupMenu);
            _menu.Items.Add(openUsage);
            _menu.Items.Add(resetPosition);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exit);
            ContextMenuStrip = _menu;

            if (!_demoMode)
            {
                _tray = new NotifyIcon
                {
                    Text = "Codex Usage Widget",
                    Icon = CreateTrayIcon(),
                    Visible = true,
                    ContextMenuStrip = _menu
                };
                _tray.DoubleClick += delegate { ShowWidget(); };
            }
        }

        private void ScaleInterface(float factor)
        {
            foreach (Control control in Controls)
                ScaleControlTree(control, factor);

            ClientSize = new Size(
                (int)Math.Round(ClientSize.Width * factor),
                (int)Math.Round(ClientSize.Height * factor));
        }

        private static void ScaleControlTree(Control control, float factor)
        {
            control.Bounds = new Rectangle(
                (int)Math.Round(control.Left * factor),
                (int)Math.Round(control.Top * factor),
                Math.Max(1, (int)Math.Round(control.Width * factor)),
                Math.Max(1, (int)Math.Round(control.Height * factor)));

            if (control.Font != null)
            {
                Font previous = control.Font;
                control.Font = new Font(previous.FontFamily, previous.Size * factor, previous.Style, GraphicsUnit.Pixel);
            }

            var sparkline = control as SparklineControl;
            if (sparkline != null)
                sparkline.VisualScale = factor;

            foreach (Control child in control.Controls)
                ScaleControlTree(child, factor);
        }

        private Icon CreateTrayIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var background = new SolidBrush(_background))
            using (var accent = new Pen(_purple, 3.5f))
            using (var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(_text))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(background, 1, 1, 30, 30);
                graphics.DrawEllipse(accent, 3, 3, 26, 26);
                graphics.DrawString("C", font, brush, 7, 5);
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        private void LoadDemoData()
        {
            DateTime now = DateTime.UtcNow;
            var demo = new List<UsageSample>();
            double credits = 1781;
            for (int seconds = 3600; seconds >= 0; seconds -= 30)
            {
                double minutesAgo = seconds / 60.0;
                if (Math.Abs(minutesAgo - 48.0) < 0.1) credits -= 4;
                if (Math.Abs(minutesAgo - 38.0) < 0.1) credits -= 15;
                if (Math.Abs(minutesAgo - 28.0) < 0.1) credits -= 40;
                if (Math.Abs(minutesAgo - 18.0) < 0.1) credits -= 90;
                if (Math.Abs(minutesAgo - 10.5) < 0.1) credits -= 5;
                demo.Add(new UsageSample
                {
                    TimestampUtc = now.AddSeconds(-seconds),
                    UsedPercent = 100,
                    Credits = credits
                });
            }
            var snapshot = new UsageSnapshot
            {
                TimestampUtc = now,
                UsedPercent = 100,
                ResetAtUtc = now.AddDays(3).AddHours(18),
                Credits = demo[demo.Count - 1].Credits,
                PlanType = "pro"
            };
            ApplySnapshot(snapshot, demo);
            SetStatus("Live");
        }

        public void ApplySnapshot(UsageSnapshot snapshot)
        {
            if (!_demoMode && _refreshCadence.Observe(_lastSnapshot, snapshot, DateTime.UtcNow))
                ApplyRefreshInterval();
            _history.Add(snapshot);
            ApplySnapshot(snapshot, _history.Samples);
        }

        private void EnterActiveRefresh()
        {
            if (_demoMode) return;
            _refreshCadence.NotifyUsage(DateTime.UtcNow);
            ApplyRefreshInterval();
        }

        private void ApplyRefreshInterval()
        {
            if (_timer == null) return;
            int desired = _refreshCadence.GetIntervalMs(DateTime.UtcNow);
            if (_timer.Interval != desired)
                _timer.Interval = desired;
        }

        private void ApplySnapshot(UsageSnapshot snapshot, List<UsageSample> samples)
        {
            _lastSnapshot = snapshot;
            double remaining = snapshot.RemainingPercent;
            _weeklyValue.Text = FormatPercent(remaining) + " left";
            _progressFill.Width = Math.Max(0, Math.Min(_progressTrack.Width, (int)Math.Round(_progressTrack.Width * remaining / 100.0)));
            _progressFill.BackColor = remaining <= 10 ? Color.FromArgb(255, 102, 82) : remaining <= 30 ? Color.FromArgb(244, 184, 71) : _green;

            _creditValue.Text = snapshot.Credits.HasValue
                ? UsageFormatting.CreditBalance(snapshot.Credits.Value)
                : "—";
            _creditCaption.Text = snapshot.Credits.HasValue ? "Available balance" : "No credit balance reported";

            UpdateCountdown();
            BurnMetrics burn = BurnCalculator.Calculate(samples, DateTime.UtcNow);
            _burnBadge.Text = burn.Level;
            _burnBadge.ForeColor = burn.Color;
            _burnBadge.BackColor = Blend(_background, burn.Color, 0.18f);
            _burnValue.Text = burn.DisplayRate;
            _peakValue.Text = UsageFormatting.PeakRate(burn.PeakRate, burn.CreditMode);
            _sparkline.SetData(samples);
            _updated.Text = "Updated " + snapshot.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) + Environment.NewLine +
                "Drag to move  •  right-click for options";
        }

        private void UpdateCountdown()
        {
            if (_lastSnapshot == null || _lastSnapshot.ResetAtUtc == DateTime.MinValue)
            {
                _resetValue.Text = "Waiting for usage data";
                return;
            }

            TimeSpan remaining = _lastSnapshot.ResetAtUtc - DateTime.UtcNow;
            if (remaining.TotalSeconds <= 0)
                _resetValue.Text = "Reset due — refreshing";
            else if (remaining.TotalDays >= 1)
                _resetValue.Text = "Resets in " + (int)remaining.TotalDays + "d " + remaining.Hours + "h";
            else if (remaining.TotalHours >= 1)
                _resetValue.Text = "Resets in " + (int)remaining.TotalHours + "h " + remaining.Minutes + "m";
            else
                _resetValue.Text = "Resets in " + Math.Max(1, remaining.Minutes) + "m";
        }

        private void SetStatus(string status)
        {
            bool live = string.Equals(status, "Live", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Connected", StringComparison.OrdinalIgnoreCase);
            _status.Text = live ? "● LIVE" : "● " + status.ToUpperInvariant();
            _status.ForeColor = live ? _green : _muted;
            if (!live && status.Length > 18)
            {
                _status.Text = "● ATTENTION";
                var tip = new ToolTip();
                tip.SetToolTip(_status, status);
                _updated.Text = status;
            }
        }

        private void SafeUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch { }
            }
            else action();
        }

        private void RestorePosition()
        {
            Point? saved = _history.LoadPosition();
            if (saved.HasValue && Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Rectangle(saved.Value, Size))))
                Location = saved.Value;
            else
                PositionAtDefault();
        }

        private void PositionAtDefault()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 22, area.Top + 22);
            _history.SavePosition(Location);
        }

        private void ShowWidget()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void WireDragging(Control control)
        {
            if (control == null || control is ContextMenuStrip) return;
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                _dragging = true;
                _dragOrigin = Cursor.Position;
            };
            control.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!_dragging || e.Button != MouseButtons.Left) return;
                Point current = Cursor.Position;
                Location = new Point(Location.X + current.X - _dragOrigin.X, Location.Y + current.Y - _dragOrigin.Y);
                _dragOrigin = current;
            };
            control.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (!_dragging) return;
                _dragging = false;
                _history.SavePosition(Location);
            };
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", false))
                    return key != null && key.GetValue("CodexUsageWidget") != null;
            }
            catch { return false; }
        }

        private bool SetStartupEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (key == null) return false;
                    if (enabled)
                        key.SetValue("CodexUsageWidget", "\"" + Application.ExecutablePath + "\"");
                    else
                        key.DeleteValue("CodexUsageWidget", false);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            try
            {
                int corner = (int)Math.Round(18 * DisplayScale);
                IntPtr region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, corner, corner);
                Region next = Region.FromHrgn(region);
                DeleteObject(region);
                Region previous = Region;
                Region = next;
                if (previous != null) previous.Dispose();
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_timer != null) _timer.Dispose();
            if (_client != null) _client.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            if (_menu != null) _menu.Dispose();
            base.OnFormClosed(e);
        }

        private static Label NewLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                Font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel),
                ForeColor = color,
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
        }

        private static string FormatPercent(double value)
        {
            if (value > 0 && value < 1) return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            return value.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static Color Blend(Color baseColor, Color overlay, float amount)
        {
            return Color.FromArgb(
                (int)(baseColor.R + (overlay.R - baseColor.R) * amount),
                (int)(baseColor.G + (overlay.G - baseColor.G) * amount),
                (int)(baseColor.B + (overlay.B - baseColor.B) * amount));
        }
    }

    internal static class Diagnostics
    {
        public static int Run(string outputPath)
        {
            var done = new ManualResetEvent(false);
            UsageSnapshot snapshot = null;
            string status = "Starting";
            using (var client = new CodexUsageClient())
            {
                client.SnapshotReceived += value => { snapshot = value; done.Set(); };
                client.StatusChanged += value => { status = value; if (value.IndexOf("sign in", StringComparison.OrdinalIgnoreCase) >= 0) done.Set(); };
                client.Start();
                done.WaitOne(TimeSpan.FromSeconds(20));
            }

            try
            {
                var payload = new Dictionary<string, object>();
                payload["success"] = snapshot != null;
                payload["status"] = status;
                if (snapshot != null)
                {
                    payload["remainingPercent"] = snapshot.RemainingPercent;
                    payload["usedPercent"] = snapshot.UsedPercent;
                    payload["credits"] = snapshot.Credits;
                    payload["resetAtUtc"] = snapshot.ResetAtUtc.ToString("o", CultureInfo.InvariantCulture);
                }
                File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(payload), Encoding.UTF8);
                return snapshot != null ? 0 : 2;
            }
            catch { return 3; }
        }
    }

    internal static class ScreenshotRenderer
    {
        public static int Render(string outputPath)
        {
            try
            {
                using (var form = new UsageWidgetForm(true))
                {
                    form.Opacity = 1;
                    form.Show();
                    Application.DoEvents();
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            catch { return 1; }
        }
    }

    internal static class SelfTests
    {
        public static int Run(string outputPath)
        {
            var messages = new List<string>();
            try
            {
                var serializer = new JavaScriptSerializer();
                var root = serializer.DeserializeObject("{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"resetsAt\":1790330425},\"credits\":{\"balance\":\"123.5\"},\"planType\":\"pro\"}}") as Dictionary<string, object>;
                UsageSnapshot parsed = RateLimitParser.ParseResult(root);
                Assert(parsed != null, "Parser returned a snapshot", messages);
                Assert(Math.Abs(parsed.RemainingPercent - 75) < 0.001, "Remaining percentage is calculated", messages);
                Assert(parsed.Credits.HasValue && Math.Abs(parsed.Credits.Value - 123.5) < 0.001, "Credit balance is parsed", messages);

                DateTime now = DateTime.UtcNow;
                var samples = new List<UsageSample>
                {
                    new UsageSample { TimestampUtc = now.AddSeconds(-30), UsedPercent = 100, Credits = 100 },
                    new UsageSample { TimestampUtc = now, UsedPercent = 100, Credits = 80 }
                };
                BurnMetrics burn = BurnCalculator.Calculate(samples, now);
                Assert(burn.Level == "MODERATE", "A recent 20-credit draw is moderate", messages);
                Assert(Math.Abs(burn.RecentDraw - 20) < 0.001, "Recent draw is calculated correctly", messages);
                Assert(Math.Abs(burn.PeakRate - 40) < 0.001 && UsageFormatting.PeakRate(burn.PeakRate, true) == "40 credits/min",
                    "Peak credit rate is calculated and formatted", messages);

                var veryHighSamples = new List<UsageSample>
                {
                    new UsageSample { TimestampUtc = now.AddSeconds(-30), UsedPercent = 100, Credits = 300 },
                    new UsageSample { TimestampUtc = now, UsedPercent = 100, Credits = 100 }
                };
                BurnMetrics veryHigh = BurnCalculator.Calculate(veryHighSamples, now);
                Assert(veryHigh.Level == "VERY HIGH" && veryHigh.Color == UsageIntensity.VeryHigh,
                    "A 200-credit draw is very high", messages);
                Assert(UsageIntensity.LevelFor(0, true) == "IDLE" &&
                    UsageIntensity.LevelFor(10, true) == "LIGHT" &&
                    UsageIntensity.LevelFor(30, true) == "MODERATE" &&
                    UsageIntensity.LevelFor(90, true) == "HEAVY" &&
                    UsageIntensity.LevelFor(180, true) == "VERY HIGH",
                    "Graph activity rates map to all five intensity colours", messages);

                var idleSamples = new List<UsageSample>
                {
                    new UsageSample { TimestampUtc = now.AddMinutes(-4.5), UsedPercent = 100, Credits = 100 },
                    new UsageSample { TimestampUtc = now.AddMinutes(-4), UsedPercent = 100, Credits = 80 },
                    new UsageSample { TimestampUtc = now, UsedPercent = 100, Credits = 80 }
                };
                BurnMetrics idle = BurnCalculator.Calculate(idleSamples, now);
                Assert(idle.Level == "IDLE", "Old draw returns to idle", messages);
                Assert(Math.Abs(idle.RecentDraw - 20) < 0.001 && idle.DisplayRate.Contains("20 credits") && idle.DisplayRate.Contains("4m"),
                    "Idle display retains the latest completed draw and its age", messages);
                Assert(Math.Abs(idle.PeakRate - 40) < 0.001,
                    "Idle display retains the completed draw's peak rate", messages);

                var separateBurstSamples = new List<UsageSample>
                {
                    new UsageSample { TimestampUtc = now.AddMinutes(-6), UsedPercent = 100, Credits = 120 },
                    new UsageSample { TimestampUtc = now.AddMinutes(-5.5), UsedPercent = 100, Credits = 100 },
                    new UsageSample { TimestampUtc = now.AddMinutes(-2), UsedPercent = 100, Credits = 100 },
                    new UsageSample { TimestampUtc = now.AddMinutes(-1.5), UsedPercent = 100, Credits = 92 },
                    new UsageSample { TimestampUtc = now, UsedPercent = 100, Credits = 92 }
                };
                BurnMetrics separateBurst = BurnCalculator.Calculate(separateBurstSamples, now);
                Assert(Math.Abs(separateBurst.RecentDraw - 8) < 0.001,
                    "A new burst replaces the earlier completed draw", messages);
                Assert(Math.Abs(separateBurst.PeakRate - 16) < 0.001,
                    "A new burst replaces the earlier peak rate", messages);

                var activitySamples = new List<UsageSample>
                {
                    new UsageSample { TimestampUtc = now.AddSeconds(-9), UsedPercent = 100, Credits = 100 },
                    new UsageSample { TimestampUtc = now.AddSeconds(-6), UsedPercent = 100, Credits = 98 },
                    new UsageSample { TimestampUtc = now.AddSeconds(-3), UsedPercent = 100, Credits = 98 },
                    new UsageSample { TimestampUtc = now, UsedPercent = 100, Credits = 98 }
                };
                List<double> activity = ActivitySeriesCalculator.Build(activitySamples);
                Assert(activity.Count == 4 && activity[1] > 0 && activity[2] == 0 && activity[3] == 0,
                    "Activity graph returns to baseline after the draw stops", messages);

                var restartGapSamples = new List<UsageSample>
                {
                    new UsageSample { TimestampUtc = now.AddMinutes(-10), UsedPercent = 100, Credits = 100 },
                    new UsageSample { TimestampUtc = now, UsedPercent = 100, Credits = 80 }
                };
                BurnMetrics restartGap = BurnCalculator.Calculate(restartGapSamples, now);
                Assert(restartGap.Level == "IDLE", "A restart gap is not reported as live activity", messages);

                var settlementSamples = new List<UsageSample>
                {
                    new UsageSample { TimestampUtc = now.AddMinutes(-2), UsedPercent = 100, Credits = 100 },
                    new UsageSample { TimestampUtc = now.AddSeconds(-30), UsedPercent = 100, Credits = 99.97 },
                    new UsageSample { TimestampUtc = now, UsedPercent = 100, Credits = 99.97 }
                };
                BurnMetrics settlement = BurnCalculator.Calculate(settlementSamples, now);
                Assert(settlement.Level == "IDLE", "Tiny settlement adjustments remain idle", messages);

                var allowanceSamples = new List<UsageSample>
                {
                    new UsageSample { TimestampUtc = now.AddSeconds(-30), UsedPercent = 20, Credits = 100 },
                    new UsageSample { TimestampUtc = now, UsedPercent = 20.5, Credits = 100 }
                };
                BurnMetrics allowanceBurn = BurnCalculator.Calculate(allowanceSamples, now);
                Assert(!allowanceBurn.CreditMode, "Included allowance is measured before credits", messages);
                Assert(allowanceBurn.Level == "MODERATE", "A recent half-point allowance draw is moderate", messages);
                Assert(UsageFormatting.CreditBalance(2290.05299488) == 2291.ToString("N0", CultureInfo.CurrentCulture), "Credit display matches desktop upward rounding", messages);

                var cadence = new AdaptiveRefreshCadence();
                Assert(cadence.GetIntervalMs(now) == AdaptiveRefreshCadence.IdleIntervalMs, "Refresh cadence starts idle", messages);
                var beforeUsage = new UsageSnapshot { TimestampUtc = now.AddSeconds(-3), UsedPercent = 100, Credits = 100 };
                var afterUsage = new UsageSnapshot { TimestampUtc = now, UsedPercent = 100, Credits = 98 };
                Assert(cadence.Observe(beforeUsage, afterUsage, now), "Credit draw activates fast refresh", messages);
                Assert(cadence.GetIntervalMs(now.AddSeconds(89)) == AdaptiveRefreshCadence.ActiveIntervalMs, "Fast refresh stays active during cooldown", messages);
                Assert(cadence.GetIntervalMs(now.AddSeconds(91)) == AdaptiveRefreshCadence.IdleIntervalMs, "Refresh cadence returns to idle", messages);
                var noiseCadence = new AdaptiveRefreshCadence();
                var afterNoise = new UsageSnapshot { TimestampUtc = now, UsedPercent = 100, Credits = 99.75 };
                Assert(!noiseCadence.Observe(beforeUsage, afterNoise, now), "Small credit settlement does not activate fast refresh", messages);
                Assert(noiseCadence.GetIntervalMs(now) == AdaptiveRefreshCadence.IdleIntervalMs, "Settlement noise keeps idle refresh", messages);

                string csvPath = Path.Combine(Path.GetTempPath(), "CodexUsageWidget-selftest-" + Guid.NewGuid().ToString("N") + ".csv");
                try
                {
                    var csv = new UsageCsvLogger(csvPath);
                    var csvStart = new UsageSample { TimestampUtc = now.AddSeconds(-3), UsedPercent = 100, Credits = 100 };
                    var csvDraw = new UsageSample { TimestampUtc = now, UsedPercent = 100, Credits = 98 };
                    var csvAfterGap = new UsageSample { TimestampUtc = now.AddSeconds(60), UsedPercent = 100, Credits = 90 };
                    csv.Append(new UsageSample { TimestampUtc = now.AddMinutes(-5), UsedPercent = 100, Credits = 120 }, csvStart);
                    csv.Append(csvStart, csvDraw);
                    csv.Append(csvDraw, csvAfterGap);

                    string[] csvLines = File.ReadAllLines(csvPath);
                    Assert(csvLines.Length == 4, "CSV logger writes one header and one row per sample", messages);
                    Assert(csvLines[0].StartsWith("TimestampLocal,TimestampUTC,CreditBalance"), "CSV log has Excel-friendly headers", messages);
                    string[] baselineFields = csvLines[1].Split(',');
                    Assert(baselineFields[3] == "" && baselineFields[5] == "", "A new CSV starts with a clean baseline", messages);
                    string[] drawFields = csvLines[2].Split(',');
                    Assert(drawFields.Length == 9, "CSV log writes all analysis columns", messages);
                    Assert(drawFields[4] == "2" && drawFields[6] == "40", "CSV log calculates interval draw and credits per minute", messages);
                    string[] gapFields = csvLines[3].Split(',');
                    Assert(gapFields[4] == "" && gapFields[6] == "", "CSV log does not invent a burst across an offline gap", messages);
                }
                finally
                {
                    try { if (File.Exists(csvPath)) File.Delete(csvPath); } catch { }
                }

                messages.Add("PASS");
                if (!string.IsNullOrEmpty(outputPath)) File.WriteAllLines(outputPath, messages.ToArray());
                return 0;
            }
            catch (Exception ex)
            {
                messages.Add("FAIL: " + ex.Message);
                try { if (!string.IsNullOrEmpty(outputPath)) File.WriteAllLines(outputPath, messages.ToArray()); } catch { }
                return 1;
            }
        }

        private static void Assert(bool condition, string name, List<string> messages)
        {
            if (!condition) throw new InvalidOperationException(name);
            messages.Add("OK: " + name);
        }
    }
}
