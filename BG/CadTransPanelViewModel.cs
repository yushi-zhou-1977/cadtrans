using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MyPlugin
{
    /// <summary>
    /// CadTrans 面板视图模型
    /// 遵循 MVVM 模式，所有界面交互逻辑集中于此
    /// </summary>
    public class CadTransPanelViewModel : INotifyPropertyChanged
    {
        #region 语言代码映射表

        // 语言列表：0=简体中文, 1=越南语, 2=印尼语, 3=英语, 4=日语, 5=韩语, 6=泰语, 7=法语, 8=德语, 9=西班牙语, 10=俄语
        private static readonly string[] LangCodes = { "zh-CN", "vi", "id", "en", "ja", "ko", "th", "fr", "de", "es", "ru" };

        #endregion

        #region 私有字段

        private int _sourceLangIndex;
        private int _targetLangIndex;
        private int _translateModeIndex;
        private string _licenseTypeDisplay = "未激活";
        private string _quotaSourceText = "试用额度";
        private string _email;
        private string _licenseKey;
        private string _machineFingerprint;
        private string _statusText;
        private string _quotaUsed = "--";
        private string _quotaTotal = "--";
        private double _quotaPercent;

        #endregion

        #region 构造函数

        public CadTransPanelViewModel()
        {
            // 加载持久化设置
            MyCommands.LoadSettings();

            // 从静态变量同步当前设置到面板
            _sourceLangIndex = Array.IndexOf(LangCodes, MyCommands.LastSourceLang);
            if (_sourceLangIndex < 0) _sourceLangIndex = 1; // 默认越南语

            _targetLangIndex = Array.IndexOf(LangCodes, MyCommands.LastTargetLang);
            if (_targetLangIndex < 0) _targetLangIndex = 0; // 默认简体中文

            _translateModeIndex = MyCommands.LastReplaceMode ? 1 : 0;
            _email = MyCommands.UserEmail ?? "";
            _licenseKey = LicenseManager.GetStoredLicenseKey() ?? "";
            _machineFingerprint = LicenseManager.GetMachineFingerprint();
            _statusText = "点击\"查看授权状态\"查询授权信息";

            // 初始化命令绑定
            QuickTranslateCommand = new RelayCommand(ExecuteQuickTranslate);
            FullTranslateCommand = new RelayCommand(ExecuteFullTranslate);
            SaveSettingsCommand = new RelayCommand(ExecuteSaveSettings);
            ActivateLicenseCommand = new RelayCommand(ExecuteActivateLicense);
            CheckLicenseCommand = new RelayCommand(ExecuteCheckLicense);
            CopyFingerprintCommand = new RelayCommand(ExecuteCopyFingerprint);

            // 面板打开时自动查询额度
            RefreshQuota();
        }

        #endregion

        #region 绑定属性

        /// <summary>源语言选择索引</summary>
        public int SourceLangIndex
        {
            get => _sourceLangIndex;
            set { _sourceLangIndex = value; OnPropertyChanged(); }
        }

        /// <summary>目标语言选择索引</summary>
        public int TargetLangIndex
        {
            get => _targetLangIndex;
            set { _targetLangIndex = value; OnPropertyChanged(); }
        }

        /// <summary>翻译模式选择索引（0=新建，1=替换）</summary>
        public int TranslateModeIndex
        {
            get => _translateModeIndex;
            set { _translateModeIndex = value; OnPropertyChanged(); }
        }

        /// <summary>用户邮箱</summary>
        public string Email
        {
            get => _email;
            set { _email = value; OnPropertyChanged(); }
        }

        /// <summary>授权类型显示文本</summary>
        public string LicenseTypeDisplay
        {
            get => _licenseTypeDisplay;
            set { _licenseTypeDisplay = value; OnPropertyChanged(); }
        }

        /// <summary>额度来源文本</summary>
        public string QuotaSourceText
        {
            get => _quotaSourceText;
            set { _quotaSourceText = value; OnPropertyChanged(); }
        }

        /// <summary>许可证密钥</summary>
        public string LicenseKey
        {
            get => _licenseKey;
            set { _licenseKey = value; OnPropertyChanged(); }
        }

        /// <summary>机器指纹码</summary>
        public string MachineFingerprint
        {
            get => _machineFingerprint;
            set { _machineFingerprint = value; OnPropertyChanged(); }
        }

        /// <summary>状态显示文本</summary>
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        /// <summary>今日已用字符数</summary>
        public string QuotaUsed
        {
            get => _quotaUsed;
            set { _quotaUsed = value; OnPropertyChanged(); }
        }

        /// <summary>今日总额度字符数</summary>
        public string QuotaTotal
        {
            get => _quotaTotal;
            set { _quotaTotal = value; OnPropertyChanged(); }
        }

        /// <summary>额度使用百分比</summary>
        public double QuotaPercent
        {
            get => _quotaPercent;
            set { _quotaPercent = value; OnPropertyChanged(); }
        }

        #endregion

        #region 命令属性

        public ICommand QuickTranslateCommand { get; }
        public ICommand FullTranslateCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand ActivateLicenseCommand { get; }
        public ICommand CheckLicenseCommand { get; }
        public ICommand CopyFingerprintCommand { get; }

        #endregion

        #region 命令实现

        /// <summary>
        /// 快速翻译：越南语->简体中文，新建模式
        /// </summary>
        private void ExecuteQuickTranslate()
        {
            try
            {
                // 设定快速翻译默认参数
                MyCommands.LastSourceLang = "vi";
                MyCommands.LastTargetLang = "zh-CN";
                MyCommands.LastReplaceMode = false;

                // 同步面板界面选择状态
                SourceLangIndex = 1;  // 越南语
                TargetLangIndex = 0;  // 简体中文
                TranslateModeIndex = 0;

                // 通过CAD命令执行，确保在正确的上下文中进行文本选择
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.SendStringToExecute("TranslateFromPanel\n", true, false, true);
                }

                // 翻译后延迟刷新额度（等翻译完成）
                System.Threading.Tasks.Task.Delay(3000).ContinueWith(t => RefreshQuota());
            }
            catch (System.Exception ex)
            {
                ShowErrorMessage("快速翻译启动失败", ex.Message);
            }
        }

        /// <summary>
        /// 完整翻译：使用面板中选择的语言和模式
        /// 通过 SendStringToExecute 在CAD命令上下文中执行
        /// </summary>
        private void ExecuteFullTranslate()
        {
            try
            {
                // 从面板选择应用语言设置
                if (SourceLangIndex >= 0 && SourceLangIndex < LangCodes.Length)
                    MyCommands.LastSourceLang = LangCodes[SourceLangIndex];

                if (TargetLangIndex >= 0 && TargetLangIndex < LangCodes.Length)
                    MyCommands.LastTargetLang = LangCodes[TargetLangIndex];

                MyCommands.LastReplaceMode = TranslateModeIndex == 1;

                // 通过CAD命令执行
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.SendStringToExecute("TranslateFromPanel\n", true, false, true);
                }

                // 翻译后延迟刷新额度（等翻译完成）
                System.Threading.Tasks.Task.Delay(3000).ContinueWith(t => RefreshQuota());
            }
            catch (System.Exception ex)
            {
                ShowErrorMessage("翻译启动失败", ex.Message);
            }
        }

        /// <summary>
        /// 保存设置：将面板中的设置值写入静态变量
        /// </summary>
        private void ExecuteSaveSettings()
        {
            try
            {
                MyCommands.UserEmail = _email ?? "";

                // 保存设置到文件，下次打开自动恢复
                MyCommands.SaveSettings();

                StatusText = string.Format(
                    "设置已保存\n邮箱: {0}\n当前额度: {1} 字符/天",
                    string.IsNullOrEmpty(_email) ? "未填写（50K套餐需要邮箱）" : _email,
                    MyCommands.GetCurrentDailyQuota());

                // 邮箱变更后刷新额度
                RefreshQuota();
            }
            catch (System.Exception ex)
            {
                StatusText = "保存设置失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 激活许可证：保存密钥并在线验证
        /// 网络请求在后台线程执行，结果通过Dispatcher回传UI
        /// </summary>
        private void ExecuteActivateLicense()
        {
            try
            {
                string key = (_licenseKey ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    StatusText = "请输入许可证密钥";
                    return;
                }

                StatusText = "正在验证许可证，请稍候...";

                // 后台线程执行网络验证，避免卡死UI
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (LicenseManager.SaveLicenseKey(key))
                        {
                            string message;
                            var status = LicenseManager.CheckLicense(out message);

                            DispatchOnUIThread(() =>
                            {
                                if (status == LicenseManager.LicenseStatus.Valid)
                                {
                                    StatusText = "授权验证成功！\n" + message;
                                }
                                else
                                {
                                    StatusText = "授权验证失败\n" + message;
                                    LicenseManager.SaveLicenseKey("");
                                }
                            });
                        }
                    }
                    catch (System.Exception ex)
                    {
                        DispatchOnUIThread(() =>
                        {
                            StatusText = "验证出错: " + ex.Message;
                        });
                    }
                });
            }
            catch (System.Exception ex)
            {
                StatusText = "激活失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 查看授权状态：检查授权信息并查询翻译额度
        /// 网络请求在后台线程执行
        /// </summary>
        private void ExecuteCheckLicense()
        {
            try
            {
                StatusText = "正在查询授权信息，请稍候...";

                // 后台线程执行网络查询
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        string message;
                        var status = LicenseManager.CheckLicense(out message);
                        string fingerprint = LicenseManager.GetMachineFingerprint();
                        string keyStatus = string.IsNullOrEmpty(LicenseManager.GetStoredLicenseKey())
                            ? "未设置" : "已设置";

                        // 查询翻译额度
                        string quotaInfo = "";
                        using (var service = new TranslationService(500,
                            MyCommands.GetCurrentDailyQuota() >= 50000 ? MyCommands.UserEmail : "",
                            MyCommands.GetCurrentDailyQuota()))
                        {
                            quotaInfo = service.CheckQuota();
                        }

                        string result = string.Format(
                            "授权状态: {0}\n状态码: {1}\n机器指纹: {2}\n密钥: {3}\n\n翻译额度:\n{4}\n\n邮箱: {5}",
                            message,
                            status,
                            fingerprint,
                            keyStatus,
                            quotaInfo,
                            string.IsNullOrEmpty(MyCommands.UserEmail)
                                ? "未设置（5千字符/天）" : MyCommands.UserEmail);

                        DispatchOnUIThread(() =>
                        {
                            StatusText = result;
                        });
                    }
                    catch (System.Exception ex)
                    {
                        DispatchOnUIThread(() =>
                        {
                            StatusText = "查询失败: " + ex.Message;
                        });
                    }
                });
            }
            catch (System.Exception ex)
            {
                StatusText = "查询出错: " + ex.Message;
            }
        }

        #endregion

        /// <summary>
        /// 复制机器指纹码到剪贴板
        /// </summary>
        private void ExecuteCopyFingerprint()
        {
            try
            {
                Clipboard.SetText(_machineFingerprint ?? "");
                StatusText = "机器指纹码已复制到剪贴板！\n请将此指纹码发送给管理员，管理员会为您生成许可证密钥。";
            }
            catch (System.Exception ex)
            {
                StatusText = "复制失败: " + ex.Message + "\n指纹码: " + _machineFingerprint;
            }
        }

        #region 辅助方法

        /// <summary>
        /// 刷新翻译额度显示（使用本地计数，无需调用API）
        /// </summary>
        public void RefreshQuota()
        {
            try
            {
                long quota = MyCommands.GetCurrentDailyQuota();
                string email = (quota >= 50000) ? (MyCommands.UserEmail ?? "") : "";

                using (var service = new TranslationService(500, email, quota))
                {
                    long used = service.GetUsedChars();
                    long total = service.GetTotalLimit();
                    double percent = total > 0 ? (double)used / total * 100.0 : 0;

                    DispatchOnUIThread(() =>
                    {
                        QuotaUsed = used.ToString("N0");
                        QuotaTotal = total.ToString("N0");
                        QuotaPercent = System.Math.Min(100.0, percent);

                        string licType = LicenseManager.GetLicenseType();
                        LicenseTypeDisplay = string.IsNullOrEmpty(licType) ? "试用" : licType;
                        QuotaSourceText = quota >= 50000 ? "50K套餐" : "5K套餐";
                    });
                }
            }
            catch (System.Exception)
            {
                DispatchOnUIThread(() =>
                {
                    QuotaUsed = "--";
                    QuotaTotal = "--";
                    QuotaPercent = 0;
                });
            }
        }

        /// <summary>
        /// 解析MyMemory返回的额度信息，提取已用/总额数字
        /// 示例格式: "YOU USED 1234 OUT OF 5000 CHARACTERS TODAY"
        /// 或: "PLEASE SELECT OPTIONAL ACTIVE TRANSLATION ENGINE 1 OUT OF 5000"
        /// </summary>
        private void ParseQuotaInfo(string quotaInfo)
        {
            try
            {
                if (string.IsNullOrEmpty(quotaInfo))
                {
                    QuotaUsed = "--";
                    QuotaTotal = "--";
                    QuotaPercent = 0;
                    return;
                }

                // 提取 "X OUT OF Y" 模式中的数字
                var match = System.Text.RegularExpressions.Regex.Match(
                    quotaInfo.ToUpper(),
                    @"(\d[\d,]*)\s*OUT\s*OF\s*(\d[\d,]*)");

                if (match.Success)
                {
                    string usedStr = match.Groups[1].Value.Replace(",", "");
                    string totalStr = match.Groups[2].Value.Replace(",", "");

                    if (long.TryParse(usedStr, out long used) && long.TryParse(totalStr, out long total) && total > 0)
                    {
                        QuotaUsed = used.ToString("N0");
                        QuotaTotal = total.ToString("N0");
                        QuotaPercent = System.Math.Min(100.0, (double)used / total * 100.0);
                        return;
                    }
                }

                // 如果没有匹配到数字，显示原始文本
                QuotaUsed = "--";
                QuotaTotal = quotaInfo.Length > 30 ? quotaInfo.Substring(0, 30) + "..." : quotaInfo;
                QuotaPercent = 0;
            }
            catch (System.Exception)
            {
                QuotaUsed = "--";
                QuotaTotal = "--";
                QuotaPercent = 0;
            }
        }

        /// <summary>
        /// 在UI线程上执行操作（线程安全更新界面）
        /// </summary>
        private void DispatchOnUIThread(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
            }
            else
            {
                action();
            }
        }

        /// <summary>
        /// 显示错误消息（使用MessageBox而非CAD的ShowAlertDialog，避免非模态上下文问题）
        /// </summary>
        private void ShowErrorMessage(string title, string detail)
        {
            DispatchOnUIThread(() =>
            {
                MessageBox.Show(title + ": " + detail, "CadTrans",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        #endregion

        #region INotifyPropertyChanged 实现

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    /// <summary>
    /// 通用中继命令实现
    /// 将ICommand绑定到委托，支持CanExecute判定
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute?.Invoke() ?? true;
        }

        public void Execute(object parameter)
        {
            _execute();
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
