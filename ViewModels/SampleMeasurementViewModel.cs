using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 用于前端 UI 绑定的测量组数据模型
    /// </summary>
    public class MeasurementGroup : ObservableObject
    {
        public int IntegrationTime { get; set; }
        public int AverageCount { get; set; }
        public ObservableCollection<string> Elements { get; set; } = new();
    }

    /// <summary>
    /// 连续进样分析视图模型
    /// </summary>
    public partial class SampleMeasurementViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;
        private readonly ElementConfigViewModel _elementConfigVM;
        private readonly PeakTrackingService _peakTracker;

        #region 1. UI 绑定属性

        [ObservableProperty] private ObservableCollection<SampleItemModel> _measurementSequence = new(); // 左侧样品序列
        [ObservableProperty] private SampleItemModel? _currentSample; // 当前正在测量或选中的样品
        [ObservableProperty] private bool _isCollecting; // 是否正在采集数据
        [ObservableProperty] private ObservableCollection<string> _pickedElements = new(); // 下拉框中显示的已识别元素名
        [ObservableProperty] private string _selectedElement = string.Empty; // 当前下拉框选中的元素

        [ObservableProperty] private MeasurementGroup _currentMeasurementGroup = new(); // 当前右上角展示的元素测量组

        // 测量强度预览：当前选中的预览元素模型
        [ObservableProperty] private ElementConcentrationModel? _selectedPreviewElement;

        // --- 【核心修复】：补齐之前遗漏声明的 5 个多次自动测试的核心属性 ---
        [ObservableProperty] private int _currentMeasurementIndex = 0; // 当前进行到第几次测量
        [ObservableProperty] private int _totalMeasurementCount = 3;  // 本次样品的总测量次数（自动绑定到Repeats）
        [ObservableProperty] private string _collectionProgressText = "就绪 - 等待启动采集"; // 状态进度提示
        [ObservableProperty] private string _detailedReadingsText = "暂无独立测试记录。"; // 各次测量值文本摘要

        partial void OnCurrentSampleChanged(SampleItemModel? value)
        {
            UpdateMeasurementGroupDisplay();

            // 每次切换样品时，自动初始化该样品下每个元素的重复测量轮次(Reps)
            if (value != null)
            {
                foreach (var ec in value.ElementConcentrations)
                {
                    if (ec.Reps.Count != value.Repeats)
                    {
                        ec.Reps.Clear();
                        for (int i = 1; i <= value.Repeats; i++)
                        {
                            ec.Reps.Add(new MeasurementRepModel { RepIndex = i, Intensity = null, IsMeasuring = false });
                        }
                    }
                }

                // 默认选中第一个元素进行数据展示
                if (value.ElementConcentrations.Any())
                {
                    SelectedPreviewElement = value.ElementConcentrations.First();
                }
            }
            else
            {
                SelectedPreviewElement = null;
            }
        }

        #endregion

        #region 2. 内部数据缓冲区

        // 收集当前单次测量周期内高频光谱帧强度的临时缓冲区
        private readonly Dictionary<string, List<double>> _currentRunFrameBuffer = new();

        // 存储已经完成的各次测量的最终平均光强历史
        private readonly Dictionary<string, List<double>> _completedRunsIntensityHistory = new();

        #endregion

        #region 3. 构造函数与初始化

        public SampleMeasurementViewModel(JsonConfigService configService, ElementConfigViewModel elementConfigVM, PeakTrackingService peakTracker)
        {
            _configService = configService;
            _elementConfigVM = elementConfigVM;
            _peakTracker = peakTracker;

            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) =>
            {
                MeasurementSequence = new ObservableCollection<SampleItemModel>(m.Value);
                if (MeasurementSequence.Count > 0)
                {
                    CurrentSample = MeasurementSequence[0];
                }

                PickedElements.Clear();
                if (CurrentSample != null)
                {
                    foreach (var ec in CurrentSample.ElementConcentrations)
                    {
                        PickedElements.Add(ec.ElementName);
                    }
                }
                if (PickedElements.Count > 0) SelectedElement = PickedElements[0];
            });
        }

        #endregion

        #region 4. 元素匹配与硬件同步逻辑

        public void ExtractIntensityFromFullSpectrum(double[] wavelengths, double[] intensities)
        {
        }

        // 公开寻峰大管家属性
        public PeakTrackingService PeakTracker => _peakTracker;

        private void UpdateMeasurementGroupDisplay()
        {
            if (CurrentSample == null || _elementConfigVM.SelectedConfigs == null || _elementConfigVM.SelectedConfigs.Count == 0)
            {
                CurrentMeasurementGroup = new MeasurementGroup();
                return;
            }

            var firstConfig = _elementConfigVM.SelectedConfigs.FirstOrDefault();
            if (firstConfig == null) return;

            int currentIntegrationTime = firstConfig.IntegrationTime;
            int currentAverageCount = firstConfig.AveragingCount;

            var groupedElements = _elementConfigVM.SelectedConfigs
                .Where(c => c.IntegrationTime == currentIntegrationTime && c.AveragingCount == currentAverageCount)
                .Select(c => $"{c.ElementName}({c.Wavelength})")
                .ToList();

            bool isBlankSample = CurrentSample.Type == SampleType.空白;
            var distinctConfigGroupsCount = _elementConfigVM.SelectedConfigs
                .Select(c => new { c.IntegrationTime, c.AveragingCount })
                .Distinct()
                .Count();

            if (isBlankSample && distinctConfigGroupsCount <= 1)
            {
                groupedElements.Clear();
            }

            CurrentMeasurementGroup = new MeasurementGroup
            {
                IntegrationTime = currentIntegrationTime,
                AverageCount = currentAverageCount,
                Elements = new ObservableCollection<string>(groupedElements)
            };
        }

        public string MatchElement(double peakedWavelength)
        {
            double tolerance = 3.0;
            var matched = _elementConfigVM.SelectedConfigs
                .Select(c => new { Config = c, Diff = Math.Abs(c.Wavelength - peakedWavelength) })
                .Where(x => x.Diff <= tolerance)
                .OrderBy(x => x.Diff)
                .FirstOrDefault();

            if (matched != null)
            {
                return $"{matched.Config.ElementName}({matched.Config.Wavelength})";
            }
            return $"峰@{peakedWavelength:F2}";
        }

        public double GetTargetWavelength()
        {
            if (string.IsNullOrEmpty(SelectedElement)) return 0;

            var config = _elementConfigVM.SelectedConfigs.FirstOrDefault(x => $"{x.ElementName}({x.Wavelength})" == SelectedElement || x.ElementName == SelectedElement);
            if (config != null) return config.Wavelength;

            if (SelectedElement.Contains("@"))
            {
                string wlStr = SelectedElement.Split('@')[1].Replace("nm", "").Trim();
                return double.TryParse(wlStr, out double wl) ? wl : 0;
            }
            return 0;
        }

        #endregion

        #region 5. 自动测量与10幅全谱图积分均值逻辑

        [RelayCommand]
        private async Task StartCollecting()
        {
            if (CurrentSample == null) { MessageBox.Show("请先选择左侧的样品！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (PickedElements.Count == 0) { MessageBox.Show("请先在主界面选择要监控的元素特征！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            if (SpectrometerManager.Instance.Devices.Count == 0)
            {
                MessageBox.Show("未检测到在线的光谱仪，请检查设备连接后再试！", "硬件未就绪", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SpectrometerManager.Instance.Devices.Any(d => !d.IsMeasuring))
            {
                MessageBox.Show("光谱仪已连接，但尚未启动持续测量推流！\n请确保主界面光谱图正在刷新再开始测试。", "采集未启动", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"当前测量样品：{CurrentSample.SampleName}");
            sb.AppendLine($"样品类型：{CurrentSample.Type}");
            sb.AppendLine($"设定的单瓶测试次数：{CurrentSample.Repeats} 次");
            sb.AppendLine("\n请确认好物理流路，点击“确定”开始自动化测量。");

            var result = MessageBox.Show(sb.ToString(), "开始实测核对", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (result != MessageBoxResult.OK) return;

            IsCollecting = true;
            await RunAutomatedMeasurementAsync();
        }

        /// <summary>
        /// 自适应多元素分组测量流程 (已添加硬件参数熔断保护，防止硬件卡死)
        /// </summary>
        private async Task RunAutomatedMeasurementAsync()
        {
            try
            {
                CollectionProgressText = "正在初始化测量任务...";
                
                if (CurrentSample == null) return;
                
                // 预先计算本瓶样品的总测试次数，用于 UI 进度展示
                var groupCount = _elementConfigVM.SelectedConfigs.GroupBy(c => new { c.IntegrationTime, c.AveragingCount }).Count();
                var repeats = CurrentSample.Repeats > 0 ? CurrentSample.Repeats : 3;
                TotalMeasurementCount = groupCount * repeats;
                CurrentMeasurementIndex = 0;

                CurrentSample.Status = "采集数据中...";

                // 1. 按相同的积分时间和平均次数对元素进行自动分组（同配置的元素一次性测量）
                var groups = _elementConfigVM.SelectedConfigs
                    .GroupBy(c => new { c.IntegrationTime, c.AveragingCount })
                    .ToList();

                var cachedColumns = new List<(string, SpectralData)>();

                // --- 循环：遍历硬件参数组 ---
                foreach (var group in groups)
                {
                    if (!IsCollecting) break;

                    int intTime = group.Key.IntegrationTime;
                    int avgCount = group.Key.AveragingCount;

                    // 下发寄存器配置
                    foreach (var device in SpectrometerManager.Instance.Devices)
                    {
                        int result = await device.UpdateConfigurationAsync(intTime, (uint)avgCount);

                        // 【核心安全熔断】
                        if (result != 0)
                        {
                            MessageBox.Show(
                                $"光谱仪 [{device.Config.SerialNumber}] 无法应用当前的硬件参数设置！\n\n" +
                                $"拒绝参数：积分时间 = {intTime} ms, 平均次数 = {avgCount}\n" +
                                $"请调整元素配置，然后再试！",
                                "硬件拒绝参数 - 采集已熔断", MessageBoxButton.OK, MessageBoxImage.Error);

                            IsCollecting = false;
                            CurrentSample.Status = "等待";
                            CollectionProgressText = "参数被硬件拒绝，测量已安全中止。";
                            return;
                        }
                    }

                    // 物理转换延时（给予硬件 3 倍测量周期的稳定时间）
                    int hwDelayMs = 3 * intTime * avgCount;
                    CollectionProgressText = $"正在应用该组元素的硬件配置 (积分 {intTime}ms, 平均 {avgCount}次)，等待硬件稳定...";
                    await Task.Delay(hwDelayMs);

                    int runCount = CurrentSample.Repeats > 0 ? CurrentSample.Repeats : 3;

                    // --- 循环：执行重复测试 ---
                    for (int r = 0; r < runCount; r++)
                    {
                        if (!IsCollecting) break;
                        
                        CurrentMeasurementIndex++;

                        // 将当前测量的 Rep 状态置为正在采集
                        var activeRepModels = new List<MeasurementRepModel>();
                        foreach (var conf in group)
                        {
                            string matchName = $"{conf.ElementName}({conf.Wavelength})";
                            var targetRow = CurrentSample.ElementConcentrations
                                .FirstOrDefault(e => e.ElementName == matchName || e.ElementName == conf.ElementName);
                            if (targetRow != null && r < targetRow.Reps.Count)
                            {
                                targetRow.Reps[r].IsMeasuring = true;
                                activeRepModels.Add(targetRow.Reps[r]);
                            }
                        }

                        // 核心：直接读取底层硬件设定好的一次光谱数据（积分时间*平均次数）
                        CollectionProgressText = $"样品 [{CurrentSample.SampleName}] - 组内采集 {r + 1}/{runCount}: 正在采集底层硬件数据包...";

                        var runFramesBuffer = group.ToDictionary(
                            c => $"{c.ElementName}({c.Wavelength})",
                            _ => new List<double>()
                        );

                        if (!IsCollecting) break;

                        SpectralData frame = await WaitForNextFrameAsync();

                        if (frame != null)
                        {
                            string elementsHeader = string.Join("|", group.Select(c => $"{c.ElementName}({c.Wavelength})"));
                            cachedColumns.Add(($"{elementsHeader}_Rep{r + 1}_Frame1", frame));

                            foreach (var conf in group)
                            {
                                double realWl = SpectrometerLogic.GetActualPeakWavelength(frame, conf.Wavelength, 1.0);
                                double realIntensity = SpectrometerLogic.GetIntensityAtWavelength(frame, realWl);

                                string key = $"{conf.ElementName}({conf.Wavelength})";
                                runFramesBuffer[key].Add(realIntensity);
                            }
                        }

                        // 复位测量指示灯
                        foreach (var rep in activeRepModels) rep.IsMeasuring = false;

                        if (!IsCollecting) break;

                        // --- 核心：剔除异常值算法 (IQR 算法) 与均值计算 ---
                        foreach (var conf in group)
                        {
                            string key = $"{conf.ElementName}({conf.Wavelength})";
                            var targetRow = CurrentSample.ElementConcentrations
                                .FirstOrDefault(e => e.ElementName == key || e.ElementName == conf.ElementName);

                            if (targetRow != null && r < targetRow.Reps.Count)
                            {
                                var list = runFramesBuffer[key];
                                if (list.Count > 0)
                                {
                                    // 1. 数据量极少时，直接取平均
                                    if (list.Count <= 3)
                                    {
                                        targetRow.Reps[r].Intensity = Math.Round(list.Average(), 2);
                                    }
                                    else
                                    {
                                        // 2. 使用四分位距 (IQR) 算法动态剔除异常跳点 (例如气泡或火花导致的突变)
                                        var sortedList = list.OrderBy(x => x).ToList();
                                        double q1 = sortedList[sortedList.Count / 4];
                                        double q3 = sortedList[sortedList.Count * 3 / 4];
                                        double iqr = q3 - q1;
                                        
                                        // IQR 乘数，1.5 是统计学标准，表示温和剔除；可调大以放松过滤
                                        double lowerBound = q1 - 1.5 * iqr;
                                        double upperBound = q3 + 1.5 * iqr;

                                        var validData = sortedList.Where(x => x >= lowerBound && x <= upperBound).ToList();
                                        
                                        // 防止全被剔除的极端情况兜底
                                        if (validData.Count == 0) validData = sortedList;

                                        targetRow.Reps[r].Intensity = Math.Round(validData.Average(), 2);
                                    }
                                }
                            }
                        }

                        // 实时计算当前已获取数据点的全局平均和 RSD
                        UpdateSelectedPreviewSummary();
                        
                        // 多次测量间的极短缓冲，避免UI冻结
                        if (r < runCount - 1) await Task.Delay(100);
                    }
                }

                if (IsCollecting)
                {
                    // 统计计算样品各元素行整体强度值与 RSD
                    CalculateOverallAveragesAndRsds();
                    CurrentSample.Status = "已完成";

                    // 后台极速静态 CSV 物理导出
                    string folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Records");
                    string filePath = System.IO.Path.Combine(folder, $"{CurrentSample.SampleName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                    var colsToSave = new List<(string, SpectralData)>(cachedColumns); // 捕获当前样品的副本
                    _ = Task.Run(async () =>
                    {
                        try { await CsvExportService.ExportSpectralDataColumnsAsync(filePath, colsToSave); }
                        catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"全谱CSV物理导出失败: {ex.Message}")); }
                    });

                    // 本地 JSON 序列保存
                    _configService.SaveResults(MeasurementSequence.ToList());

                    // 【新增】：每个样品测完后，立即向数据处理模块推送当前最新数据，实现真正的“实时记录”
                    WeakReferenceMessenger.Default.Send(new SampleSequenceChangedMessage(MeasurementSequence.ToList()));

                    // --- 半自动模式：自动跳到下一行并停止测量，等待用户更换溶液后重新点击 ---
                    int currentIndex = MeasurementSequence.IndexOf(CurrentSample);
                    if (currentIndex < MeasurementSequence.Count - 1)
                    {
                        CurrentSample = MeasurementSequence[currentIndex + 1];
                        CollectionProgressText = "本瓶测量完毕！请更换溶液后，再次点击【开始采集数据】以继续。";
                    }
                    else
                    {
                        CollectionProgressText = "全部样品测量完毕！请点击【实验结束并处理数据】。";
                    }
                }
            }
            finally
            {
                // 确保单次执行完毕后，主按钮状态自动弹回，允许用户重新开始
                IsCollecting = false;
                _currentRunFrameBuffer.Clear();
            }
        }

        [RelayCommand]
        private void FinishExperimentAndNavigate()
        {
            MessageBox.Show("实验已结束，正在为您跳转至数据处理界面！", "实验结束", MessageBoxButton.OK, MessageBoxImage.Information);
            WeakReferenceMessenger.Default.Send(new NavigateMessage("DataProcessing"));
        }

        private void UpdateSelectedPreviewSummary()
        {
            if (SelectedPreviewElement != null)
            {
                var validIntensities = SelectedPreviewElement.Reps
                    .Where(r => r.Intensity.HasValue)
                    .Select(r => r.Intensity.GetValueOrDefault()) // 使用 GetValueOrDefault() 彻底消除 CS8629 警告
                    .ToList();

                if (validIntensities.Count >= 2)
                {
                    double avg = validIntensities.Average();
                    double sumOfSquares = validIntensities.Select(val => (val - avg) * (val - avg)).Sum();
                    double stdDev = Math.Sqrt(sumOfSquares / (validIntensities.Count - 1));
                    double rsd = (avg != 0) ? (stdDev / avg) * 100.0 : 0;

                    SelectedPreviewElement.MeasuredIntensity = Math.Round(avg, 2);
                    SelectedPreviewElement.MeasuredRsd = Math.Round(rsd, 2);
                }
                else if (validIntensities.Count == 1)
                {
                    SelectedPreviewElement.MeasuredIntensity = Math.Round(validIntensities[0], 2);
                    SelectedPreviewElement.MeasuredRsd = 0;
                }
                else
                {
                    SelectedPreviewElement.MeasuredIntensity = 0;
                    SelectedPreviewElement.MeasuredRsd = 0;
                }
            }
        }

        private void CalculateOverallAveragesAndRsds()
        {
            foreach (var row in CurrentSample!.ElementConcentrations)
            {
                var validIntensities = row.Reps
                    .Where(r => r.Intensity.HasValue)
                    .Select(r => r.Intensity.GetValueOrDefault()) // 使用 GetValueOrDefault() 彻底消除 CS8629 警告
                    .ToList();

                if (validIntensities.Count >= 2)
                {
                    double avg = validIntensities.Average();
                    double sumOfSquares = validIntensities.Select(val => (val - avg) * (val - avg)).Sum();
                    double stdDev = Math.Sqrt(sumOfSquares / (validIntensities.Count - 1));
                    double rsd = (avg != 0) ? (stdDev / avg) * 100.0 : 0;

                    row.MeasuredIntensity = Math.Round(avg, 2);
                    row.MeasuredRsd = Math.Round(rsd, 2);
                }
                else if (validIntensities.Count == 1)
                {
                    row.MeasuredIntensity = Math.Round(validIntensities[0], 2);
                    row.MeasuredRsd = 0;
                }
            }
        }

        private async Task<SpectralData> WaitForNextFrameAsync()
        {
            var tcs = new TaskCompletionSource<SpectralData>();
            var token = new object();

            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(token, (r, m) =>
            {
                WeakReferenceMessenger.Default.Unregister<SpectralDataMessage>(token);
                tcs.TrySetResult(m.Value);
            });

            // 15 秒物理超时保护
            var timeoutTask = Task.Delay(15000).ContinueWith(_ =>
            {
                WeakReferenceMessenger.Default.Unregister<SpectralDataMessage>(token);
                tcs.TrySetResult(null!);
            });

            var result = await tcs.Task;
            
            // 【核心修复】：由于 WeakReferenceMessenger 内部只持有 token 的弱引用，
            // 必须在等待期间强引用 token，否则 GC 回收后会导致永远收不到帧回调而引发长达15秒的卡死！
            GC.KeepAlive(token);
            
            return result;
        }

        [RelayCommand]
        private void StopAndSave()
        {
            if (!IsCollecting) return;
            IsCollecting = false;
            CollectionProgressText = "测量已中途结束，正在保存已测得数据并统计结算...";
            CalculateOverallAveragesAndRsds();
        }

        [RelayCommand]
        private void StopSequence()
        {
            if (IsCollecting)
            {
                IsCollecting = false;
                CollectionProgressText = "自动测量已被强行终止";
                if (CurrentSample != null) CurrentSample.Status = "手动中止";
                _currentRunFrameBuffer.Clear();
            }
        }

        [RelayCommand]
        private void ClearCurrentSampleData()
        {
            if (CurrentSample == null)
            {
                MessageBox.Show("请先选择要清空数据的样品！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (IsCollecting)
            {
                MessageBox.Show("正在采集中，请先停止采集后再清空！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"确定要清空样品【{CurrentSample.SampleName}】的全部测量数据吗？\n清空后该样品将恢复为“等待”状态。", "清空确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                CurrentSample.Status = "等待";
                foreach (var ec in CurrentSample.ElementConcentrations)
                {
                    ec.MeasuredIntensity = 0;
                    ec.MeasuredRsd = 0;
                    foreach (var rep in ec.Reps)
                    {
                        rep.Intensity = null;
                        rep.IsMeasuring = false;
                    }
                }
                UpdateSelectedPreviewSummary();
                MessageBox.Show("该样品的测量数据已成功清空！您可以随时重新开始采集。", "已清空", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion

        #region 7. 数据保存快照导出

        [RelayCommand]
        private void ExportCurrentSession()
        {
            if (MeasurementSequence.Count == 0) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "实验快照文件 (*.json)|*.json",
                FileName = $"实验数据_{DateTime.Now:yyyyMMdd_HHmm}",
                Title = "导出当前测量序列及结果"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _configService.ExportResults(dialog.FileName, MeasurementSequence.ToList());
                    MessageBox.Show("当前会话数据导出成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion
    }
}