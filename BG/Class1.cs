﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Windows;
using System.Drawing;
using System.Windows.Forms.Integration;

namespace MyPlugin
{
    #region 翻译服务类

    /// <summary>
    /// 翻译API响应数据结构（MyMemory API）
    /// </summary>
    [DataContract]
    public class TranslationResponse
    {
        [DataMember(Name = "responseStatus")]
        public int ResponseStatus { get; set; }

        [DataMember(Name = "responseDetails")]
        public string ResponseDetails { get; set; }

        [DataMember(Name = "responseData")]
        public TranslationData ResponseData { get; set; }

        [DataMember(Name = "matches")]
        public List<TranslationMatch> Matches { get; set; }

        [DataMember(Name = "responseStatusDescription")]
        public string ResponseStatusDescription { get; set; }
    }

    [DataContract]
    public class TranslationData
    {
        [DataMember(Name = "translatedText")]
        public string TranslatedText { get; set; }

        [DataMember(Name = "match")]
        public double Match { get; set; }
    }

    [DataContract]
    public class TranslationMatch
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "segment")]
        public string Segment { get; set; }

        [DataMember(Name = "translation")]
        public string Translation { get; set; }

        [DataMember(Name = "quality")]
        public double Quality { get; set; }
    }

    /// <summary>
    /// 翻译服务类 - 使用MyMemory免费翻译API
    /// 支持越南文到中文的翻译
    /// 使用同步HTTP请求，避免AutoCAD主线程死锁
    /// </summary>
    public class TranslationService : IDisposable
    {
        private const string ApiBaseUrl = "https://api.mymemory.translated.net/get";
        private readonly int _requestDelayMs;
        private DateTime _lastRequestTime;
        private readonly string _email;
        private readonly long _dailyQuotaLimit;

        // 本地额度追踪
        private static long _dailyCharCount;
        private static DateTime _dailyResetDate;
        private static readonly object _quotaLock = new object();
        private const long AnonymousDailyLimit = 5000;
        private const long EmailDailyLimit = 50000;

        public TranslationService(int requestDelayMs = 500, string email = "", long dailyQuota = 5000)
        {
            _requestDelayMs = requestDelayMs;
            _lastRequestTime = DateTime.MinValue;
            _email = email ?? "";
            _dailyQuotaLimit = dailyQuota;
        }

        /// <summary>
        /// 同步翻译文本
        /// </summary>
        public string Translate(string text, string sourceLang = "vi", string targetLang = "zh-CN")
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                // 检查本地额度
                if (GetRemainingChars() <= 0)
                    return "[额度用完]";

                // 限流控制
                var timeSinceLastRequest = DateTime.Now - _lastRequestTime;
                if (timeSinceLastRequest.TotalMilliseconds < _requestDelayMs)
                {
                    System.Threading.Thread.Sleep(_requestDelayMs - (int)timeSinceLastRequest.TotalMilliseconds);
                }

                string encodedText = Uri.EscapeDataString(text);
                string url = $"{ApiBaseUrl}?q={encodedText}&langpair={sourceLang}|{targetLang}";

                // 仅50K套餐发送邮箱给MyMemory，5K套餐使用匿名模式
                if (_dailyQuotaLimit >= EmailDailyLimit && !string.IsNullOrWhiteSpace(_email))
                {
                    url += $"&de={Uri.EscapeDataString(_email)}";
                }

                // 带429重试的请求
                int maxRetries = 3;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    _lastRequestTime = DateTime.Now;

                    try
                    {
                        using (WebClient client = new WebClient { Encoding = Encoding.UTF8 })
                        {
                            string jsonResponse = client.DownloadString(url);

                            if (string.IsNullOrEmpty(jsonResponse))
                                return "[API返回空]";

                            TranslationResponse result = ParseJsonResponse(jsonResponse);
                            if (result != null && result.ResponseData != null && !string.IsNullOrEmpty(result.ResponseData.TranslatedText))
                            {
                                AddUsedChars(text.Length);
                                return result.ResponseData.TranslatedText;
                            }

                            string debugInfo = jsonResponse.Length > 100 ? jsonResponse.Substring(0, 100) : jsonResponse;
                            return "[解析失败:" + debugInfo + "]";
                        }
                    }
                    catch (System.Net.WebException wex)
                    {
                        if (wex.Message.Contains("429") && attempt < maxRetries - 1)
                        {
                            // 429限流，指数退避重试：5秒、15秒、30秒
                            int waitMs = (int)Math.Pow(3, attempt + 1) * 2000;
                            System.Threading.Thread.Sleep(waitMs);
                            continue;
                        }
                        return "[异常:" + wex.Message + "]";
                    }
                }

                return "[重试耗尽:429限流]";
            }
            catch (System.Exception ex)
            {
                return "[异常:" + ex.Message + "]";
            }
        }

        /// <summary>
        /// 获取今日已用字符数
        /// </summary>
        public long GetUsedChars()
        {
            lock (_quotaLock)
            {
                ResetIfNeeded();
                return _dailyCharCount;
            }
        }

        /// <summary>
        /// 获取今日总额度
        /// </summary>
        public long GetTotalLimit()
        {
            return _dailyQuotaLimit;
        }

        /// <summary>
        /// 获取剩余字符数
        /// </summary>
        public long GetRemainingChars()
        {
            lock (_quotaLock)
            {
                ResetIfNeeded();
                return GetTotalLimit() - _dailyCharCount;
            }
        }

        /// <summary>
        /// 累加已用字符数
        /// </summary>
        private void AddUsedChars(int count)
        {
            lock (_quotaLock)
            {
                ResetIfNeeded();
                _dailyCharCount += count;
            }
        }

        /// <summary>
        /// 如果日期变更，重置计数器
        /// </summary>
        private void ResetIfNeeded()
        {
            DateTime today = DateTime.Today;
            if (_dailyResetDate != today)
            {
                _dailyCharCount = 0;
                _dailyResetDate = today;
            }
        }

        /// <summary>
        /// 查询翻译额度（本地计数，无需调用API）
        /// </summary>
        public string CheckQuota()
        {
            lock (_quotaLock)
            {
                ResetIfNeeded();
                long total = GetTotalLimit();
                double percent = total > 0 ? (double)_dailyCharCount / total * 100.0 : 0;
                return string.Format("YOU USED {0} OUT OF {1} CHARACTERS TODAY ({2:F1}%)",
                    _dailyCharCount, total, percent);
            }
        }

        /// <summary>
        /// 解析JSON响应
        /// </summary>
        private TranslationResponse ParseJsonResponse(string jsonResponse)
        {
            try
            {
                // 直接提取translatedText，避免DataContractJsonSerializer的兼容性问题
                string translatedText = ExtractTranslatedText(jsonResponse);
                if (!string.IsNullOrEmpty(translatedText))
                {
                    return new TranslationResponse
                    {
                        ResponseData = new TranslationData
                        {
                            TranslatedText = translatedText
                        }
                    };
                }
                return null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从JSON中提取translatedText字段
        /// 支持 \uXXXX Unicode转义
        /// </summary>
        private string ExtractTranslatedText(string json)
        {
            // 查找 "translatedText":"..."
            const string key = "\"translatedText\":\"";
            int startIndex = json.IndexOf(key, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                // 尝试查找 null 值
                const string nullKey = "\"translatedText\":null";
                if (json.IndexOf(nullKey, StringComparison.Ordinal) >= 0)
                    return null;
                return null;
            }

            startIndex += key.Length;

            // 手动提取字符串值，处理转义字符
            var sb = new System.Text.StringBuilder();
            int i = startIndex;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    if (next == 'u' && i + 5 < json.Length)
                    {
                        // \uXXXX Unicode转义
                        string hex = json.Substring(i + 2, 4);
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int codePoint))
                        {
                            sb.Append((char)codePoint);
                            i += 6;
                            continue;
                        }
                    }
                    else if (next == 'n') { sb.Append('\n'); i += 2; continue; }
                    else if (next == 'r') { sb.Append('\r'); i += 2; continue; }
                    else if (next == 't') { sb.Append('\t'); i += 2; continue; }
                    else if (next == '"') { sb.Append('"'); i += 2; continue; }
                    else if (next == '\\') { sb.Append('\\'); i += 2; continue; }
                    else if (next == '/') { sb.Append('/'); i += 2; continue; }
                }
                else if (c == '"')
                {
                    // 字符串结束
                    break;
                }
                else
                {
                    sb.Append(c);
                }
                i++;
            }

            return sb.ToString();
        }

        public void Dispose()
        {
        }
    }

    #endregion

    #region 授权管理类

    public class LicenseManager
    {
        private const string LicenseServerUrl = "https://cadtrans.pages.dev/api";
        private const string LicenseKeyFileName = "CadTrans.license";
        private const int TrialDays = 1;
        private const long TrialDailyQuota = 5000;
        private const int CacheValidHours = 24;

        private static string _licenseKey = "";
        private static bool _isValidated = false;
        private static DateTime _lastValidationTime = DateTime.MinValue;
        private static LicenseStatus _currentStatus = LicenseStatus.Unknown;
        private static long _cachedDailyQuota = 5000;
        private static string _cachedLicenseType = "";

        public enum LicenseStatus
        {
            Unknown,
            Trial,
            Valid,
            Expired,
            Invalid,
            NotActivated,
            HardwareMismatch
        }

        public static LicenseStatus CurrentStatus
        {
            get { return _currentStatus; }
        }

        public static long GetDailyQuota()
        {
            return _cachedDailyQuota;
        }

        public static string GetLicenseType()
        {
            return _cachedLicenseType;
        }

        private static string GetLicenseCachePath()
        {
            return Path.Combine(Path.GetDirectoryName(GetLicenseFilePath()), "license_cache.dat");
        }

        public static void SaveLicenseCache(long dailyQuota, string type)
        {
            try
            {
                File.WriteAllText(GetLicenseCachePath(), $"{dailyQuota}\n{type ?? ""}");
            }
            catch { }
        }

        public static void LoadLicenseCache()
        {
            try
            {
                string path = GetLicenseCachePath();
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length >= 1) _cachedDailyQuota = long.Parse(lines[0]);
                    if (lines.Length >= 2) _cachedLicenseType = lines[1];
                }
            }
            catch { }
        }

        public static string GetMachineFingerprint()
        {
            try
            {
                string cpuId = GetCPUId();
                string diskId = GetDiskId();
                string baseStr = $"{cpuId}_{diskId}";
                
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(baseStr));
                    return BitConverter.ToString(hash).Replace("-", "").ToLower();
                }
            }
            catch
            {
                return "unknown";
            }
        }

        private static string GetCPUId()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["ProcessorId"]?.ToString() ?? "cpu_unknown";
                    }
                }
            }
            catch { }
            return "cpu_unknown";
        }

        private static string GetDiskId()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive WHERE InterfaceType='IDE'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["SerialNumber"]?.ToString()?.Trim() ?? "disk_unknown";
                    }
                }
            }
            catch { }
            return "disk_unknown";
        }

        private static string GetLicenseFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "CadTrans");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return Path.Combine(dir, LicenseKeyFileName);
        }

        public static string GetStoredLicenseKey()
        {
            try
            {
                string path = GetLicenseFilePath();
                if (File.Exists(path))
                    return File.ReadAllText(path).Trim();
            }
            catch { }
            return "";
        }

        public static bool SaveLicenseKey(string key)
        {
            try
            {
                string path = GetLicenseFilePath();
                File.WriteAllText(path, key.Trim());
                _licenseKey = key.Trim();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static LicenseStatus CheckLicense(out string message)
        {
            message = "";

            if (DateTime.Now - _lastValidationTime < TimeSpan.FromHours(CacheValidHours) && _isValidated)
            {
                message = "授权验证通过（缓存）";
                return LicenseStatus.Valid;
            }

            _licenseKey = GetStoredLicenseKey();

            if (string.IsNullOrWhiteSpace(_licenseKey))
            {
                return CheckTrial(out message);
            }

            return ValidateLicenseOnline(_licenseKey, out message);
        }

        private static LicenseStatus CheckTrial(out string message)
        {
            message = "";
            string trialFile = Path.Combine(Path.GetDirectoryName(GetLicenseFilePath()), "trial.dat");

            if (!File.Exists(trialFile))
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                File.WriteAllText(trialFile, $"StartDate={today}\nDailyChars=0\nLastReset={today}");
            }

            try
            {
                string content = File.ReadAllText(trialFile);
                string[] lines = content.Split('\n');
                DateTime startDate = DateTime.Parse(lines[0].Split('=')[1]);
                long dailyChars = long.Parse(lines[1].Split('=')[1]);
                DateTime lastReset = lines.Length >= 3 ? DateTime.Parse(lines[2].Split('=')[1]) : startDate;

                if (DateTime.Now > startDate.AddDays(TrialDays))
                {
                    message = "试用期限已过（1天），请购买正式授权。";
                    _currentStatus = LicenseStatus.Expired;
                    return LicenseStatus.Expired;
                }

                // 日期切换时重置日额度
                if (DateTime.Today > lastReset.Date)
                {
                    dailyChars = 0;
                    lastReset = DateTime.Today;
                    File.WriteAllText(trialFile,
                        $"StartDate={startDate:yyyy-MM-dd}\nDailyChars=0\nLastReset={lastReset:yyyy-MM-dd}");
                }

                if (dailyChars >= TrialDailyQuota)
                {
                    message = $"试用每日额度已用完（{TrialDailyQuota}字符/天），请购买正式授权。";
                    _currentStatus = LicenseStatus.Expired;
                    return LicenseStatus.Expired;
                }

                int remainingDays = (int)(startDate.AddDays(TrialDays) - DateTime.Now).TotalDays + 1;
                long remainingChars = TrialDailyQuota - dailyChars;
                message = $"试用模式：剩余 {remainingDays} 天，今日剩余 {remainingChars} 字符";
                _currentStatus = LicenseStatus.Trial;
                return LicenseStatus.Trial;
            }
            catch
            {
                message = "试用数据读取失败";
                _currentStatus = LicenseStatus.Unknown;
                return LicenseStatus.Unknown;
            }
        }

        public static void UpdateTrialUsage(int charCount)
        {
            string trialFile = Path.Combine(Path.GetDirectoryName(GetLicenseFilePath()), "trial.dat");
            if (!File.Exists(trialFile)) return;

            try
            {
                string content = File.ReadAllText(trialFile);
                string[] lines = content.Split('\n');
                DateTime startDate = DateTime.Parse(lines[0].Split('=')[1]);
                long dailyChars = long.Parse(lines[1].Split('=')[1]);
                DateTime lastReset = lines.Length >= 3 ? DateTime.Parse(lines[2].Split('=')[1]) : startDate;

                // 日期切换时重置
                if (DateTime.Today > lastReset.Date)
                {
                    dailyChars = 0;
                    lastReset = DateTime.Today;
                }

                dailyChars += charCount;
                File.WriteAllText(trialFile,
                    $"StartDate={startDate:yyyy-MM-dd}\nDailyChars={dailyChars}\nLastReset={lastReset:yyyy-MM-dd}");
            }
            catch { }
        }

        private static LicenseStatus ValidateLicenseOnline(string key, out string message)
        {
            message = "";

            try
            {
                string fingerprint = GetMachineFingerprint();
                string url = $"{LicenseServerUrl}/validate?key={Uri.EscapeDataString(key)}&fingerprint={Uri.EscapeDataString(fingerprint)}";

                using (WebClient client = new WebClient { Encoding = Encoding.UTF8 })
                {
                    string response = client.DownloadString(url);
                    LicenseResponse result = ParseLicenseResponse(response);

                    if (result != null)
                    {
                        if (result.Valid)
                        {
                            _isValidated = true;
                            _lastValidationTime = DateTime.Now;
                            _currentStatus = LicenseStatus.Valid;

                            // 缓存授权额度信息
                            _cachedDailyQuota = result.DailyQuota > 0 ? result.DailyQuota : 5000;
                            _cachedLicenseType = result.LicenseType ?? "";
                            SaveLicenseCache(_cachedDailyQuota, _cachedLicenseType);

                            message = $"授权有效，到期时间: {result.ExpireDate}，每日额度: {_cachedDailyQuota}字符";
                            return LicenseStatus.Valid;
                        }
                        else
                        {
                            _currentStatus = GetStatusFromMessage(result.Message);
                            message = result.Message;
                            return _currentStatus;
                        }
                    }
                }
            }
            catch (WebException)
            {
                message = "无法连接授权服务器，请检查网络。";
                _currentStatus = LicenseStatus.Unknown;
            }
            catch (System.Exception)
            {
                message = "授权验证失败。";
                _currentStatus = LicenseStatus.Invalid;
            }

            return _currentStatus;
        }

        private static LicenseStatus GetStatusFromMessage(string msg)
        {
            if (msg.Contains("过期")) return LicenseStatus.Expired;
            if (msg.Contains("不匹配")) return LicenseStatus.HardwareMismatch;
            return LicenseStatus.Invalid;
        }

        private static LicenseResponse ParseLicenseResponse(string json)
        {
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(LicenseResponse));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (LicenseResponse)serializer.ReadObject(stream);
                }
            }
            catch
            {
                return SimpleParseLicenseResponse(json);
            }
        }

        private static LicenseResponse SimpleParseLicenseResponse(string json)
        {
            try
            {
                bool valid = json.Contains("\"valid\":true") || json.Contains("\"Valid\":true");
                string expireDate = ExtractValue(json, "expireDate", "ExpireDate");
                string msg = ExtractValue(json, "message", "Message");
                long dailyQuota = ExtractNumber(json, "dailyQuota", "DailyQuota");
                string licenseType = ExtractValue(json, "type", "Type");

                return new LicenseResponse
                {
                    Valid = valid,
                    ExpireDate = expireDate,
                    Message = msg,
                    DailyQuota = dailyQuota,
                    LicenseType = licenseType
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractValue(string json, params string[] keys)
        {
            foreach (string key in keys)
            {
                string searchKey = $"\"{key}\":\"";
                int idx = json.IndexOf(searchKey);
                if (idx >= 0)
                {
                    int start = idx + searchKey.Length;
                    int end = json.IndexOf("\"", start);
                    if (end > start)
                        return json.Substring(start, end - start);
                }
            }
            return "";
        }

        private static long ExtractNumber(string json, params string[] keys)
        {
            foreach (string key in keys)
            {
                string searchKey = $"\"{key}\":";
                int idx = json.IndexOf(searchKey);
                if (idx >= 0)
                {
                    int start = idx + searchKey.Length;
                    string remaining = json.Substring(start).TrimStart();
                    int end = 0;
                    while (end < remaining.Length && (char.IsDigit(remaining[end]) || remaining[end] == '-'))
                        end++;
                    if (end > 0 && long.TryParse(remaining.Substring(0, end), out long val))
                        return val;
                }
            }
            return 0;
        }

        [DataContract]
        public class LicenseResponse
        {
            [DataMember(Name = "valid")]
            public bool Valid { get; set; }

            [DataMember(Name = "expireDate")]
            public string ExpireDate { get; set; }

            [DataMember(Name = "message")]
            public string Message { get; set; }

            [DataMember(Name = "dailyQuota")]
            public long DailyQuota { get; set; }

            [DataMember(Name = "type")]
            public string LicenseType { get; set; }
        }
    }

    #endregion

    public class MyCommands
    {
        #region 翻译功能

        // 翻译功能设置（internal 供 CadTransPanelViewModel 访问）
        internal static string LastSourceLang = "vi";         // 默认越南语
        internal static string LastTargetLang = "zh-CN";      // 默认简体中文
        internal static bool LastReplaceMode = false;         // 默认创建新文本而非替换
        internal static string UserEmail = "";                // MyMemory API邮箱

        // 获取当前每日翻译额度
        internal static long GetCurrentDailyQuota()
        {
            if (LicenseManager.CurrentStatus == LicenseManager.LicenseStatus.Valid)
                return LicenseManager.GetDailyQuota();
            if (LicenseManager.CurrentStatus == LicenseManager.LicenseStatus.Trial)
                return 5000;
            return 5000;
        }

        // 设置文件路径
        private static readonly string SettingsFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CadTrans", "settings.json");

        // 标记是否已加载设置
        private static bool _settingsLoaded = false;

        /// <summary>
        /// 加载持久化设置（首次访问时自动调用）
        /// </summary>
        internal static void LoadSettings()
        {
            if (_settingsLoaded) return;
            _settingsLoaded = true;

            // 加载授权缓存
            LicenseManager.LoadLicenseCache();

            try
            {
                if (!System.IO.File.Exists(SettingsFilePath)) return;

                string json = System.IO.File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                var dict = SimpleParseJsonToDict(json);

                if (dict.ContainsKey("sourceLang")) LastSourceLang = dict["sourceLang"];
                if (dict.ContainsKey("targetLang")) LastTargetLang = dict["targetLang"];
                if (dict.ContainsKey("replaceMode")) LastReplaceMode = dict["replaceMode"] == "true";
                if (dict.ContainsKey("email")) UserEmail = dict["email"];
            }
            catch (System.Exception) { }
        }

        /// <summary>
        /// 保存设置到文件
        /// </summary>
        internal static void SaveSettings()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(SettingsFilePath);
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                string json = string.Format(
                    "{{\"sourceLang\":\"{0}\",\"targetLang\":\"{1}\",\"replaceMode\":\"{2}\",\"email\":\"{3}\"}}",
                    LastSourceLang, LastTargetLang, LastReplaceMode ? "true" : "false",
                    UserEmail.Replace("\"", "\\\""));

                System.IO.File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
            }
            catch (System.Exception) { }
        }

        /// <summary>
        /// 简单JSON解析为字典
        /// </summary>
        private static Dictionary<string, string> SimpleParseJsonToDict(string json)
        {
            var dict = new Dictionary<string, string>();
            json = json.Trim().TrimStart('{').TrimEnd('}');
            foreach (string pair in json.Split(','))
            {
                int colonIndex = pair.IndexOf(':');
                if (colonIndex < 0) continue;
                string key = pair.Substring(0, colonIndex).Trim().Trim('"');
                string val = pair.Substring(colonIndex + 1).Trim().Trim('"');
                dict[key] = val;
            }
            return dict;
        }

        // PaletteSet 静态实例，避免重复创建
        private static PaletteSet _cadTransPaletteSet;

        /// <summary>
        /// 越南文翻译命令
        /// 支持DBText和MText对象的翻译
        /// </summary>
        [CommandMethod("TranslateVN")]
        public static void TranslateVietnamese()
        {
            LoadSettings();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                Application.ShowAlertDialog("没有活动的AutoCAD文档！");
                return;
            }

            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                // 0. 授权检查
                if (!CheckLicenseAndPrompt(ed))
                    return;

                // 1. 获取用户设置
                GetTranslationPreferences(ed);

                // 2. 选择文本对象
                PromptSelectionOptions pso = new PromptSelectionOptions
                {
                    MessageForAdding = "\n请选择需要翻译的文本对象（DBText或MText）: ",
                    AllowDuplicates = false,
                    RejectObjectsOnLockedLayers = true
                };

                // 过滤器：只选择DBText和MText
                SelectionFilter sf = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue(0, "TEXT,MTEXT")  // TEXT=DBText, MTEXT=MText
                });

                PromptSelectionResult psr = ed.GetSelection(pso, sf);
                if (psr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n未选择任何文本对象。");
                    return;
                }

                int totalCount = psr.Value.Count;
                ed.WriteMessage($"\n已选择 {totalCount} 个文本对象，开始翻译...");

                // 3. 执行翻译
                ExecuteTranslation(doc, db, ed, psr.Value, totalCount);
            }
            catch (System.Exception ex)
            {
                Application.ShowAlertDialog($"翻译执行错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取翻译参数设置
        /// </summary>
        private static void GetTranslationPreferences(Editor ed)
        {
            // 源语言选择
            PromptKeywordOptions langOptions = new PromptKeywordOptions("\n选择源语言: ");
            langOptions.Keywords.Add("zh-CN", "zh-CN", "简体中文(zh-CN)");
            langOptions.Keywords.Add("vi", "vi", "越南语(vi)");
            langOptions.Keywords.Add("id", "id", "印尼语(id)");
            langOptions.Keywords.Add("en", "en", "英语(en)");
            langOptions.Keywords.Add("ja", "ja", "日语(ja)");
            langOptions.Keywords.Add("ko", "ko", "韩语(ko)");
            langOptions.Keywords.Add("th", "th", "泰语(th)");
            langOptions.Keywords.Add("fr", "fr", "法语(fr)");
            langOptions.Keywords.Add("de", "de", "德语(de)");
            langOptions.Keywords.Add("es", "es", "西班牙语(es)");
            langOptions.Keywords.Add("ru", "ru", "俄语(ru)");
            langOptions.Keywords.Default = LastSourceLang;
            PromptResult langResult = ed.GetKeywords(langOptions);
            if (langResult.Status == PromptStatus.OK)
            {
                LastSourceLang = langResult.StringResult;
            }

            // 目标语言选择
            PromptKeywordOptions targetOptions = new PromptKeywordOptions("\n选择目标语言: ");
            targetOptions.Keywords.Add("zh-CN", "zh-CN", "简体中文(zh-CN)");
            targetOptions.Keywords.Add("vi", "vi", "越南语(vi)");
            targetOptions.Keywords.Add("id", "id", "印尼语(id)");
            targetOptions.Keywords.Add("en", "en", "英语(en)");
            targetOptions.Keywords.Add("ja", "ja", "日语(ja)");
            targetOptions.Keywords.Add("ko", "ko", "韩语(ko)");
            targetOptions.Keywords.Add("th", "th", "泰语(th)");
            targetOptions.Keywords.Add("fr", "fr", "法语(fr)");
            targetOptions.Keywords.Add("de", "de", "德语(de)");
            targetOptions.Keywords.Add("es", "es", "西班牙语(es)");
            targetOptions.Keywords.Add("ru", "ru", "俄语(ru)");
            targetOptions.Keywords.Default = LastTargetLang;
            PromptResult targetResult = ed.GetKeywords(targetOptions);
            if (targetResult.Status == PromptStatus.OK)
            {
                LastTargetLang = targetResult.StringResult;
            }

            // 翻译模式：替换或创建新文本
            PromptKeywordOptions modeOptions = new PromptKeywordOptions("\n选择翻译模式: ");
            modeOptions.Keywords.Add("n", "n", "新建文本(n) - 在原文本旁创建翻译");
            modeOptions.Keywords.Add("r", "r", "替换文本(r) - 直接替换原文本");
            modeOptions.Keywords.Default = LastReplaceMode ? "r" : "n";
            PromptResult modeResult = ed.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.OK)
            {
                LastReplaceMode = modeResult.StringResult.ToLower() == "r";
            }
        }

        /// <summary>
        /// 执行翻译任务（同步方式，避免AutoCAD主线程死锁）
        /// </summary>
        private static void ExecuteTranslation(
            Document doc,
            Database db,
            Editor ed,
            SelectionSet selection,
            int totalCount)
        {
            int successCount = 0;
            int failCount = 0;

            using (TranslationService translator = new TranslationService(requestDelayMs: 600,
                email: GetCurrentDailyQuota() >= 50000 ? UserEmail : "",
                dailyQuota: GetCurrentDailyQuota()))
            {
                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            BlockTableRecord modelspace = (BlockTableRecord)tr.GetObject(
                                SymbolUtilityServices.GetBlockModelSpaceId(db),
                                OpenMode.ForWrite);

                            int currentIndex = 0;
                            foreach (SelectedObject selObj in selection)
                            {
                                currentIndex++;

                                if (selObj == null) continue;

                                Entity ent = tr.GetObject(selObj.ObjectId, OpenMode.ForRead) as Entity;
                                if (ent == null) continue;

                                string originalText = GetEntityText(ent);
                                if (string.IsNullOrWhiteSpace(originalText))
                                {
                                    ed.WriteMessage($"\n[{currentIndex}/{totalCount}] 跳过空白文本");
                                    failCount++;
                                    continue;
                                }

                                ed.WriteMessage($"\n[{currentIndex}/{totalCount}] 正在翻译: {originalText.Substring(0, Math.Min(30, originalText.Length))}...");

                                try
                                {
                                    string translatedText = translator.Translate(
                                        originalText,
                                        LastSourceLang,
                                        LastTargetLang);

                                    if (string.IsNullOrEmpty(translatedText))
                                    {
                                        ed.WriteMessage(" [失败: 翻译结果为空]");
                                        failCount++;
                                        continue;
                                    }

                                    // 检查是否为调试错误信息
                                    if (translatedText.StartsWith("["))
                                    {
                                        ed.WriteMessage($" [失败: {translatedText}]");
                                        failCount++;
                                        continue;
                                    }

                                    bool created = false;
                                    if (LastReplaceMode)
                                    {
                                        created = ReplaceTextContent(tr, ent, translatedText);
                                    }
                                    else
                                    {
                                        created = CreateTranslatedText(tr, ent, translatedText, modelspace, db);
                                    }

                                    if (created)
                                    {
                                        successCount++;
                                        // 试用模式下记录字符用量
                                        if (LicenseManager.CurrentStatus == LicenseManager.LicenseStatus.Trial)
                                            LicenseManager.UpdateTrialUsage(originalText.Length);
                                        ed.WriteMessage($" -> {translatedText.Substring(0, Math.Min(30, translatedText.Length))} [成功]");
                                    }
                                    else
                                    {
                                        failCount++;
                                        ed.WriteMessage(" [失败: 创建文本失败]");
                                    }
                                }
                                catch (System.Exception tex)
                                {
                                    failCount++;
                                    ed.WriteMessage($" [翻译错误: {tex.Message}]");
                                }
                            }

                            tr.Commit();
                        }
                        catch (System.Exception ex)
                        {
                            tr.Abort();
                            ed.WriteMessage($"\n事务错误: {ex.Message}");
                        }
                    }
                }
            }

            ed.WriteMessage($"\n\n========== 翻译完成 ==========");
            ed.WriteMessage($"\n成功: {successCount} 个");
            ed.WriteMessage($"\n失败: {failCount} 个");
            ed.WriteMessage($"\n总计: {totalCount} 个");
            ed.WriteMessage($"\n================================");
        }

        /// <summary>
        /// 获取实体文本内容（清理CAD格式代码）
        /// </summary>
        private static string GetEntityText(Entity ent)
        {
            string rawText = null;
            if (ent is DBText dbText)
            {
                rawText = dbText.TextString;
            }
            else if (ent is MText mText)
            {
                rawText = mText.Text;
            }
            if (rawText == null) return null;

            return CleanCadFormatCodes(rawText);
        }

        /// <summary>
        /// 清理CAD格式代码，提取纯文本用于翻译
        /// %%U = 下划线开关, %%K = 上划线开关, %%O = 上划线开关
        /// %%D = 度符号(°), %%P = 正负号(±), %%C = 直径符号(Φ)
        /// %%nnn = ASCII字符
        /// {\fFont|b0|i0|c0|p0;...} = MText字体格式
        /// \P = 段落换行, \W = 宽度, \A = 对齐, \H = 高度, \S = 堆叠
        /// </summary>
        private static string CleanCadFormatCodes(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 处理MText格式代码
            // 移除字体格式 {\fFontName|b0|i0|c0|p0;...}
            string cleaned = System.Text.RegularExpressions.Regex.Replace(
                text, @"\\f[A-Za-z0-9_]+\|[^;]*;", "");

            // 移除MText格式命令 \A, \H, \W, \T, \Q, \L, \l, \O, \o, \K, \k, \P
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned, @"\\[AHWTQLOKokPl][^;]*;?", "");

            // 移除堆叠格式 \S...;
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned, @"\\S[^;]*;", "");

            // 移除花括号
            cleaned = cleaned.Replace("{", "").Replace("}", "");

            // 处理%%格式代码
            // %%U, %%K, %%O 是开关代码，直接移除
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned, @"%%[UKOuko]", "");

            // %%D -> °, %%P -> ±, %%C -> Φ
            cleaned = cleaned.Replace("%%D", "°");
            cleaned = cleaned.Replace("%%P", "±");
            cleaned = cleaned.Replace("%%C", "Φ");

            // %%nnn 三位数字表示ASCII字符
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned, @"%%(\d{3})", m =>
                {
                    if (int.TryParse(m.Groups[1].Value, out int asciiCode) && asciiCode >= 32 && asciiCode <= 255)
                        return ((char)asciiCode).ToString();
                    return "";
                });

            // 移除多余的空格和换行
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();

            return cleaned;
        }

        /// <summary>
        /// 替换文本内容（替换模式）
        /// </summary>
        private static bool ReplaceTextContent(Transaction tr, Entity ent, string translatedText)
        {
            try
            {
                // 将实体升级为写模式
                ent.UpgradeOpen();

                if (ent is DBText dbText)
                {
                    dbText.TextString = translatedText;
                    return true;
                }
                else if (ent is MText mText)
                {
                    mText.Contents = translatedText;
                    return true;
                }

                return false;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 创建翻译文本（新建模式）
        /// 在原文本旁边创建新的中文文本
        /// </summary>
        private static bool CreateTranslatedText(
            Transaction tr,
            Entity originalEnt,
            string translatedText,
            BlockTableRecord modelspace,
            Database db)
        {
            try
            {
                Point3d position = Point3d.Origin;
                double rotation = 0;

                // 获取或创建中文字体样式
                ObjectId textStyleId = GetOrCreateChineseTextStyle(tr, db, "SimHei");

                if (originalEnt is DBText dbText)
                {
                    position = dbText.Position;
                    rotation = dbText.Rotation;
                    double height = dbText.Height;
                    // 偏移距离 = 1倍字体高度
                    double offset = height;

                    Point3d newPosition = new Point3d(
                        position.X,
                        position.Y - offset,
                        position.Z);

                    DBText newText = new DBText();
                    newText.Position = newPosition;
                    newText.Height = height;
                    newText.Rotation = rotation;
                    newText.TextString = translatedText;
                    newText.ColorIndex = 3;

                    if (textStyleId != ObjectId.Null)
                        newText.TextStyleId = textStyleId;

                    modelspace.AppendEntity(newText);
                    tr.AddNewlyCreatedDBObject(newText, true);
                    return true;
                }
                else if (originalEnt is MText mText)
                {
                    position = mText.Location;
                    rotation = mText.Rotation;
                    double height = mText.TextHeight;
                    // 偏移距离 = 1倍字体高度
                    double offset = height;

                    Point3d newPosition = new Point3d(
                        position.X,
                        position.Y - offset,
                        position.Z);

                    MText newMText = new MText();
                    newMText.Location = newPosition;
                    newMText.TextHeight = height;
                    newMText.Rotation = rotation;
                    newMText.Contents = translatedText;
                    if (mText.Width > 0)
                        newMText.Width = mText.Width;
                    newMText.ColorIndex = 3;

                    if (textStyleId != ObjectId.Null)
                        newMText.TextStyleId = textStyleId;

                    modelspace.AppendEntity(newMText);
                    tr.AddNewlyCreatedDBObject(newMText, true);
                    return true;
                }

                return false;
            }
            catch (System.Exception ex)
            {
                Application.ShowAlertDialog($"创建文本失败: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 获取或创建中文字体样式
        /// </summary>
        private static ObjectId GetOrCreateChineseTextStyle(Transaction tr, Database db, string styleName)
        {
            try
            {
                TextStyleTable ts = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

                if (ts.Has(styleName))
                {
                    return ts[styleName];
                }

                ts.UpgradeOpen();

                TextStyleTableRecord newStyle = new TextStyleTableRecord();
                newStyle.Name = styleName;
                newStyle.FileName = "simhei.ttf";
                newStyle.XScale = 1.0;

                ts.Add(newStyle);
                tr.AddNewlyCreatedDBObject(newStyle, true);
                ts.DowngradeOpen();

                return newStyle.ObjectId;
            }
            catch (System.Exception ex)
            {
                Application.ShowAlertDialog($"创建文字样式失败: {ex.Message}");
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// 快速翻译命令 - 使用默认设置，减少用户交互
        /// </summary>
        [CommandMethod("TranslateVNQuick")]
        public static void TranslateVietnameseQuick()
        {
            LoadSettings();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                Application.ShowAlertDialog("没有活动的AutoCAD文档！");
                return;
            }

            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                // 授权检查
                if (!CheckLicenseAndPrompt(ed))
                    return;

                // 使用默认设置：越南语 -> 中文，新建文本模式
                LastSourceLang = "vi";
                LastTargetLang = "zh-CN";
                LastReplaceMode = false;

                // 选择文本对象
                PromptSelectionOptions pso = new PromptSelectionOptions
                {
                    MessageForAdding = "\n请选择需要翻译的越南文文本: ",
                    AllowDuplicates = false,
                    RejectObjectsOnLockedLayers = true
                };

                SelectionFilter sf = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue(0, "TEXT,MTEXT")
                });

                PromptSelectionResult psr = ed.GetSelection(pso, sf);
                if (psr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n未选择任何文本对象。");
                    return;
                }

                int totalCount = psr.Value.Count;
                ed.WriteMessage($"\n快速翻译模式: 越南语 -> 简体中文");
                ed.WriteMessage($"\n已选择 {totalCount} 个文本对象，开始翻译...");

                ExecuteTranslation(doc, db, ed, psr.Value, totalCount);
            }
            catch (System.Exception ex)
            {
                Application.ShowAlertDialog($"快速翻译执行错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置翻译参数命令 - 通过命令行交互设置
        /// </summary>
        [CommandMethod("SetTranslateDefaults")]
        public static void SetTranslateDefaults()
        {
            LoadSettings();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;

            try
            {
                ed.WriteMessage("\n========== 翻译参数设置 ==========");

                PromptStringOptions emailOptions = new PromptStringOptions("\n输入邮箱地址（50K套餐必需，用于提升翻译API额度）[当前: " + (string.IsNullOrEmpty(UserEmail) ? "未填写" : UserEmail) + "]: ");
                emailOptions.AllowSpaces = false;
                PromptResult emailResult = ed.GetString(emailOptions);
                if (emailResult.Status == PromptStatus.OK)
                {
                    UserEmail = emailResult.StringResult.Trim();
                }

                PromptKeywordOptions modeOptions = new PromptKeywordOptions("\n选择翻译模式 [当前: " + (LastReplaceMode ? "替换" : "新建") + "]: ");
                modeOptions.Keywords.Add("n", "n", "新建文本(n)");
                modeOptions.Keywords.Add("r", "r", "替换文本(r)");
                modeOptions.Keywords.Default = LastReplaceMode ? "r" : "n";
                PromptResult modeResult = ed.GetKeywords(modeOptions);
                if (modeResult.Status == PromptStatus.OK)
                {
                    LastReplaceMode = modeResult.StringResult.ToLower() == "r";
                }

                ed.WriteMessage("\n\n已更新翻译设置：");
                ed.WriteMessage($"\n邮箱: {(string.IsNullOrEmpty(UserEmail) ? "未填写 (5,000字符/天)" : $"{UserEmail} (50,000字符/天)")}");
                ed.WriteMessage($"\n翻译模式: {(LastReplaceMode ? "替换" : "新建")}");
                ed.WriteMessage($"\n文本高度: 自动（跟随原文）");
                ed.WriteMessage($"\n偏移距离: 自动（1倍字体高度）");
                ed.WriteMessage("\n==================================");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n设置翻译参数时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 授权检查并提示用户
        /// </summary>
        private static bool CheckLicenseAndPrompt(Editor ed)
        {
            string message;
            LicenseManager.LicenseStatus status = LicenseManager.CheckLicense(out message);

            ed.WriteMessage($"\n授权状态: {message}");

            switch (status)
            {
                case LicenseManager.LicenseStatus.Valid:
                case LicenseManager.LicenseStatus.Trial:
                    return true;

                case LicenseManager.LicenseStatus.Expired:
                case LicenseManager.LicenseStatus.Invalid:
                case LicenseManager.LicenseStatus.HardwareMismatch:
                    Application.ShowAlertDialog($"{message}\n\n请输入许可证密钥以继续使用。\n输入命令: SetLicenseKey");
                    return false;

                case LicenseManager.LicenseStatus.Unknown:
                    ed.WriteMessage("\n警告: 授权检查失败，将在试用模式下运行。");
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 输入许可证密钥命令
        /// </summary>
        [CommandMethod("SetLicenseKey")]
        public static void SetLicenseKey()
        {
            LoadSettings();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;

            try
            {
                PromptStringOptions keyOptions = new PromptStringOptions("\n请输入许可证密钥: ");
                keyOptions.AllowSpaces = false;
                PromptResult keyResult = ed.GetString(keyOptions);

                if (keyResult.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n未输入许可证密钥。");
                    return;
                }

                string key = keyResult.StringResult.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    ed.WriteMessage("\n许可证密钥不能为空。");
                    return;
                }

                if (LicenseManager.SaveLicenseKey(key))
                {
                    ed.WriteMessage("\n许可证密钥已保存，正在验证...");

                    string message;
                    LicenseManager.LicenseStatus status = LicenseManager.CheckLicense(out message);

                    if (status == LicenseManager.LicenseStatus.Valid)
                    {
                        ed.WriteMessage($"\n验证成功！{message}");
                        Application.ShowAlertDialog($"授权验证成功！\n{message}");
                    }
                    else
                    {
                        ed.WriteMessage($"\n验证失败: {message}");
                        Application.ShowAlertDialog($"授权验证失败！\n{message}");
                        LicenseManager.SaveLicenseKey("");
                    }
                }
                else
                {
                    ed.WriteMessage("\n保存许可证密钥失败。");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n设置许可证密钥时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 查看授权状态命令
        /// </summary>
        [CommandMethod("CheckLicense")]
        public static void CheckLicense()
        {
            LoadSettings();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;

            try
            {
                string message;
                LicenseManager.LicenseStatus status = LicenseManager.CheckLicense(out message);

                ed.WriteMessage("\n========== 授权状态 ==========");
                ed.WriteMessage($"\n状态: {message}");
                ed.WriteMessage($"\n机器指纹: {LicenseManager.GetMachineFingerprint()}");
                ed.WriteMessage($"\n当前密钥: {(string.IsNullOrEmpty(LicenseManager.GetStoredLicenseKey()) ? "未设置" : "已设置")}");
                ed.WriteMessage("\n==============================");

                // 查询并显示翻译额度
                using (var service = new TranslationService(1500,
                    GetCurrentDailyQuota() >= 50000 ? UserEmail : "",
                    GetCurrentDailyQuota()))
                {
                    string quotaInfo = service.CheckQuota();
                    ed.WriteMessage("\n========== 翻译额度 ==========");
                    ed.WriteMessage($"\n{quotaInfo}");
                    string licType = LicenseManager.GetLicenseType();
                    ed.WriteMessage($"\n授权类型: {(string.IsNullOrEmpty(licType) ? "试用" : licType)}");
                    ed.WriteMessage($"\n每日额度: {GetCurrentDailyQuota()} 字符");
                    ed.WriteMessage($"\n邮箱: {(UserEmail.Length > 0 ? UserEmail : "未设置")}");
                    ed.WriteMessage("\n==============================");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n检查授权状态时出错: {ex.Message}");
            }
        }

        [CommandMethod("CheckQuota")]
        public static void CheckQuota()
        {
            LoadSettings();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;

            try
            {
                ed.WriteMessage("\n正在查询翻译额度...");

                using (var service = new TranslationService(1500,
                    GetCurrentDailyQuota() >= 50000 ? UserEmail : "",
                    GetCurrentDailyQuota()))
                {
                    string quotaInfo = service.CheckQuota();

                    ed.WriteMessage("\n========== 翻译额度 ==========");
                    ed.WriteMessage($"\n{quotaInfo}");
                    string licType = LicenseManager.GetLicenseType();
                    ed.WriteMessage($"\n授权类型: {(string.IsNullOrEmpty(licType) ? "试用" : licType)}");
                    ed.WriteMessage($"\n每日额度: {GetCurrentDailyQuota()} 字符");
                    ed.WriteMessage($"\n当前邮箱: {(UserEmail.Length > 0 ? UserEmail : "未设置")}");
                    ed.WriteMessage($"\n提示: 5K套餐无需邮箱，50K套餐需设置邮箱");
                    ed.WriteMessage("\n==============================");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n查询额度时出错: {ex.Message}");
            }
        }

        #endregion

        #region WPF 面板命令

        /// <summary>
        /// 显示CadTrans翻译助手面板（非模态PaletteSet）
        /// 命令名：CadTransPanel
        /// </summary>
        [CommandMethod("CadTransPanel")]
        public static void ShowCadTransPanel()
        {
            LoadSettings();
            try
            {
                if (_cadTransPaletteSet == null)
                {
                    // 创建 PaletteSet 实例
                    _cadTransPaletteSet = new PaletteSet(
                        "CadTrans 翻译助手",
                        new Guid("A8FA5B3C-2D4E-4F6B-8A1C-3D5E7F9B1A2C"));

                    // 创建 WPF UserControl 并用 ElementHost 包装
                    // PaletteSet 的 Add 方法需要 WinForms Control，ElementHost 是 WPF 到 WinForms 的桥梁
                    CadTransPanel wpfPanel = new CadTransPanel();
                    ElementHost host = new ElementHost();
                    host.Child = wpfPanel;
                    host.Dock = System.Windows.Forms.DockStyle.Fill;

                    // 添加到 PaletteSet
                    _cadTransPaletteSet.Add("翻译助手", host);

                    // 设置面板样式和尺寸
                    _cadTransPaletteSet.Style = PaletteSetStyles.ShowAutoHideButton |
                                                 PaletteSetStyles.ShowCloseButton;
                    _cadTransPaletteSet.MinimumSize = new Size(300, 500);
                    _cadTransPaletteSet.Size = new Size(300, 600);
                }

                _cadTransPaletteSet.Visible = true;
            }
            catch (System.Exception ex)
            {
                Application.ShowAlertDialog($"打开面板失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从面板调用的翻译命令
        /// 直接使用静态变量中的设置，不再通过命令行交互提示用户
        /// 此命令通过 SendStringToExecute 触发，运行在CAD命令上下文中
        /// </summary>
        [CommandMethod("TranslateFromPanel")]
        public static void TranslateFromPanel()
        {
            LoadSettings();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                Application.ShowAlertDialog("没有活动的AutoCAD文档！");
                return;
            }

            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                // 授权检查
                if (!CheckLicenseAndPrompt(ed))
                    return;

                // 选择文本对象（不再提示语言/模式参数，直接使用面板设置的静态变量）
                PromptSelectionOptions pso = new PromptSelectionOptions
                {
                    MessageForAdding = $"\n请选择需要翻译的文本对象 ({LastSourceLang}->{LastTargetLang}, {(LastReplaceMode ? "替换" : "新建")}): ",
                    AllowDuplicates = false,
                    RejectObjectsOnLockedLayers = true
                };

                SelectionFilter sf = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue(0, "TEXT,MTEXT")
                });

                PromptSelectionResult psr = ed.GetSelection(pso, sf);
                if (psr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n未选择任何文本对象。");
                    return;
                }

                int totalCount = psr.Value.Count;
                ed.WriteMessage($"\n已选择 {totalCount} 个文本对象，开始翻译...");
                ed.WriteMessage($"\n模式: {LastSourceLang} -> {LastTargetLang} ({(LastReplaceMode ? "替换" : "新建")})");

                ExecuteTranslation(doc, db, ed, psr.Value, totalCount);
            }
            catch (System.Exception ex)
            {
                Application.ShowAlertDialog($"翻译执行错误: {ex.Message}");
            }
        }

        #endregion
    }
}

