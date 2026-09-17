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
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GD_ControlCenter_WPF.ViewModels
{
    public partial class FlowInjectionViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;
        private readonly ElementConfigViewModel _elementConfigVM;

        [ObservableProperty] private ObservableCollection<SampleItemModel> _measurementSequence = new();
        [ObservableProperty] private SampleItemModel? _currentSample;
        [ObservableProperty] private bool _isScanning; 
        
        [ObservableProperty] private string _collectionProgressText = "就绪 - 等待启动采集";



        [ObservableProperty] private ObservableCollection<string> _pickedElements = new();

        // 核心缓冲区：记录本次扫描的各元素时间序列，Key: "Element(Wavelength)"
        private Dictionary<string, List<PlotPoint>> _scanBuffers = new();
        // 缓存每个样品的完整时序图数据
        private readonly Dictionary<SampleItemModel, Dictionary<string, List<PlotPoint>>> _plotCache = new();
        private DateTime _scanStartTime;
        private int _currentScanPointStartIndex = 0;

        public FlowInjectionViewModel(JsonConfigService configService, ElementConfigViewModel elementConfigVM)
        {
            _configService = configService;
            _elementConfigVM = elementConfigVM;

            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) => {
                MeasurementSequence = new ObservableCollection<SampleItemModel>(m.Value);
                if (MeasurementSequence.Count > 0 && CurrentSample == null)
                {
                    CurrentSample = MeasurementSequence[0];
                }
                
                // 清理已经从序列中删除的样品的图谱缓存
                var currentSampleNames = MeasurementSequence.Select(s => s.SampleName).ToHashSet();
                var keysToRemove = _plotCache.Keys.Where(k => !currentSampleNames.Contains(k.SampleName)).ToList();
                foreach (var key in keysToRemove)
                {
                    _plotCache.Remove(key);
                }
            });

            WeakReferenceMessenger.Default.Register<ContinuousMeasurementStartedMessage>(this, (r, m) => {
                _plotCache.Clear();
                _scanBuffers.Clear();
            });
            
            // 初始化 PickedElements，供 View 层分配颜色
            _elementConfigVM.SelectedConfigs.CollectionChanged += (s, e) => UpdatePickedElements();
            UpdatePickedElements();
        }

        partial void OnCurrentSampleChanged(SampleItemModel? oldValue, SampleItemModel? newValue)
        {
            if (newValue != null)
            {
                if (!_plotCache.ContainsKey(newValue))
                {
                    _plotCache[newValue] = new Dictionary<string, List<PlotPoint>>();
                }
                _scanBuffers = _plotCache[newValue];
                
                // 切换样品时，批量渲染该样品历史时序图
                WeakReferenceMessenger.Default.Send(new FlowInjectionPlotBatchMessage(_scanBuffers));
            }
        }

        private void UpdatePickedElements()
        {
            PickedElements.Clear();
            foreach (var c in _elementConfigVM.SelectedConfigs)
            {
                PickedElements.Add($"{c.ElementName}({c.Wavelength})");
            }
        }

        // 获取下一帧，类似连续进样的拦截方式
        private async Task<SpectralData> WaitForNextFrameAsync()
        {
            var tcs = new TaskCompletionSource<SpectralData>();
            var token = new object();

            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(token, (r, m) =>
            {
                WeakReferenceMessenger.Default.Unregister<SpectralDataMessage>(token);
                tcs.TrySetResult(m.Value);
            });

            var timeoutTask = Task.Delay(5000).ContinueWith(_ =>
            {
                WeakReferenceMessenger.Default.Unregister<SpectralDataMessage>(token);
                tcs.TrySetResult(null!);
            });

            var result = await tcs.Task;
            GC.KeepAlive(token);
            return result;
        }



        [RelayCommand]
        private async Task StartScan()
        {
            if (CurrentSample == null)
            {
                MessageBox.Show("请先选择或添加样品！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_elementConfigVM.SelectedConfigs.Count == 0)
            {
                MessageBox.Show("请先在【元素配置】中勾选需要测量的元素！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 【一致性检查】：流动注射要求所有监控元素的参数完全一致
            var firstConfig = _elementConfigVM.SelectedConfigs.First();
            bool isConsistent = _elementConfigVM.SelectedConfigs.All(c => 
                c.IntegrationTime == firstConfig.IntegrationTime && 
                c.AveragingCount == firstConfig.AveragingCount);

            if (!isConsistent)
            {
                MessageBox.Show("流动注射模式要求所有选中元素的测量参数必须一致！\n请回到【元素配置】页面统一积分时间和平均次数。", "参数冲突", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 不再清理整个缓冲区，而是保留历史记录以拼接
            foreach (var el in PickedElements)
            {
                if (!_scanBuffers.ContainsKey(el))
                {
                    _scanBuffers[el] = new List<PlotPoint>();
                }
            }

            IsScanning = true;
            CurrentSample.Status = "正在扫描...";
            CollectionProgressText = "正在初始化流动注射硬件配置...";

            try
            {
                // 下发统一的硬件配置
                foreach (var device in SpectrometerManager.Instance.Devices)
                {
                    int result = await device.UpdateConfigurationAsync(firstConfig.IntegrationTime, (uint)firstConfig.AveragingCount);
                    if (result != 0)
                    {
                        MessageBox.Show($"光谱仪硬件配置被拒绝！\n积分时间: {firstConfig.IntegrationTime}ms\n平均次数: {firstConfig.AveragingCount}", "硬件报错", MessageBoxButton.OK, MessageBoxImage.Error);
                        IsScanning = false;
                        return;
                    }
                }

                // 硬件稳定延时
                await Task.Delay(3 * firstConfig.IntegrationTime * firstConfig.AveragingCount);

                // 记录本次注射的数据起始索引，用于后续只分析最新数据
                _currentScanPointStartIndex = _scanBuffers.Values.FirstOrDefault()?.Count ?? 0;

                // 为了让同一样品的多次注射连续拼接到时序图中，计算时间偏移量
                double timeOffset = 0;
                if (_currentScanPointStartIndex > 0)
                {
                    timeOffset = _scanBuffers.Values.First().Last().Time + 1.0; // 留出 1秒 的间隔
                }
                _scanStartTime = DateTime.Now.AddSeconds(-timeOffset);
                
                CollectionProgressText = "硬件已就绪，正在进行流动注射时序采样，请注射溶液...";

                // 核心循环：高频抽取全谱并分离各元素通道
                while (IsScanning)
                {
                    var frame = await WaitForNextFrameAsync();
                    if (frame == null) continue; // 超时或未获取到
                    
                    // 通知 View 刷新上方的全谱图
                    WeakReferenceMessenger.Default.Send(new SpectralDataMessage(frame));

                    double timeSec = (DateTime.Now - _scanStartTime).TotalSeconds;

                    foreach (var conf in _elementConfigVM.SelectedConfigs)
                    {
                        string key = $"{conf.ElementName}({conf.Wavelength})";
                        // 按照您的要求，恢复与原来寻峰算法一致的 ±1.0 nm 动态搜索大窗口
                        // 这样可以确保它能完全像常规测量一样自动爬到最高点（但也请注意如果样品里有靠得很近的强基体峰，依然可能存在吸附现象）
                        double realWl = SpectrometerLogic.GetActualPeakWavelength(frame, conf.Wavelength, 1.0);
                        double intensity = SpectrometerLogic.GetIntensityAtWavelength(frame, realWl);

                        var pt = new PlotPoint(key, timeSec, intensity);
                        _scanBuffers[key].Add(pt);
                        
                        // 通知 View 刷新下方的时序图
                        WeakReferenceMessenger.Default.Send(new FlowInjectionPlotMessage(pt));
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"扫描过程发生异常: {ex.Message}");
                IsScanning = false;
            }
        }

        [RelayCommand]
        private void StopScan()
        {
            if (!IsScanning) return;
            IsScanning = false;
            CollectionProgressText = "扫描停止，正在进行自动寻峰与面积积分...";
            
            AnalyzePeaksAndSave();
        }

        private void AnalyzePeaksAndSave()
        {
            if (CurrentSample == null) return;
            
            // 初始化 Reps (如果是第一次测这个样品)
            foreach (var ec in CurrentSample.ElementConcentrations)
            {
                if (ec.Reps == null) ec.Reps = new ObservableCollection<MeasurementRepModel>();
            }

            int startRepIndex = (CurrentSample.ElementConcentrations.Max(e => (int?)e.Reps.Count) ?? 0) + 1;

            // --- 1. 寻找最佳主参考通道 (信号最强、扣除基线后峰高最高的通道) ---
            string bestReferenceKey = null;
            double maxReferenceHeight = -1;
            double bestGlobalBaseline = 0;
            List<PlotPoint> bestCurrentPoints = null;

            var validConfigs = _elementConfigVM.SelectedConfigs.Where(c => _scanBuffers.ContainsKey($"{c.ElementName}({c.Wavelength})")).ToList();
            if (validConfigs.Count == 0) return;

            foreach (var conf in validConfigs)
            {
                string key = $"{conf.ElementName}({conf.Wavelength})";
                var currentPoints = _scanBuffers[key].Skip(_currentScanPointStartIndex).ToList();
                if (currentPoints.Count < 3) continue;

                double baseline = CalculateGlobalBaseline(currentPoints);
                double maxHeight = currentPoints.Max(p => p.Intensity) - baseline;

                if (maxHeight > maxReferenceHeight)
                {
                    maxReferenceHeight = maxHeight;
                    bestReferenceKey = key;
                    bestGlobalBaseline = baseline;
                    bestCurrentPoints = currentPoints;
                }
            }

            if (bestReferenceKey == null || bestCurrentPoints == null) return;

            // --- 2. 在主参考通道上划定独立注射的时间窗口 ---
            // 如果主通道最高峰还不到 300，说明可能全是一片空白噪声，没有有效峰，兜底当做 1 次测量
            List<Tuple<int, int>> peakWindows = new List<Tuple<int, int>>();
            
            if (maxReferenceHeight > 300)
            {
                double threshold = bestGlobalBaseline + maxReferenceHeight * 0.10; // 10% 阈值切峰
                bool inPeak = false;
                int currentPeakStart = 0;
                
                for (int i = 0; i < bestCurrentPoints.Count; i++)
                {
                    if (!inPeak && bestCurrentPoints[i].Intensity > threshold)
                    {
                        inPeak = true;
                        currentPeakStart = i;
                    }
                    else if (inPeak && (bestCurrentPoints[i].Intensity <= threshold || i == bestCurrentPoints.Count - 1))
                    {
                        inPeak = false;
                        // 过滤掉太窄的噪声毛刺 (至少要持续几个点才算有效的峰)
                        if (i - currentPeakStart > 2)
                        {
                            peakWindows.Add(new Tuple<int, int>(currentPeakStart, i));
                        }
                    }
                }
            }

            // 如果没切出任何有效的峰，或者全是空白，默认将整段作为一个大窗口
            if (peakWindows.Count == 0)
            {
                peakWindows.Add(new Tuple<int, int>(0, bestCurrentPoints.Count - 1));
            }

            // --- 3. 同步测算全通道 (统一应用时间窗口) ---
            foreach (var conf in validConfigs)
            {
                string key = $"{conf.ElementName}({conf.Wavelength})";
                var currentPoints = _scanBuffers[key].Skip(_currentScanPointStartIndex).ToList();
                if (currentPoints.Count < 3) continue;

                var targetRow = CurrentSample.ElementConcentrations.FirstOrDefault(e => e.ElementName == key || e.ElementName == conf.ElementName);
                if (targetRow == null) continue;

                double elementGlobalBaseline = CalculateGlobalBaseline(currentPoints);
                
                int repOffset = 0;
                foreach (var window in peakWindows)
                {
                    int startIndex = window.Item1;
                    int endIndex = Math.Min(window.Item2, currentPoints.Count - 1);
                    
                    if (startIndex > endIndex) continue;

                    // 在这个公共时间窗内，寻找该元素自己的最高点
                    double localMaxIntensity = elementGlobalBaseline; // 兜底为基线
                    for (int i = startIndex; i <= endIndex; i++)
                    {
                        if (currentPoints[i].Intensity > localMaxIntensity)
                        {
                            localMaxIntensity = currentPoints[i].Intensity;
                        }
                    }

                    double exactPeakHeight = localMaxIntensity - elementGlobalBaseline;
                    if (exactPeakHeight < 0) exactPeakHeight = 0;

                    int repIndex = startRepIndex + repOffset;
                    Application.Current.Dispatcher.Invoke(() => 
                    {
                        targetRow.Reps.Add(new MeasurementRepModel 
                        { 
                            RepIndex = repIndex, 
                            Intensity = Math.Round(exactPeakHeight, 2), 
                            IsMeasuring = false 
                        });
                    });
                    
                    repOffset++;
                }
            }

            // 更新总体平均值和 RSD
            CalculateOverallAveragesAndRsds();
            
            // 向外广播最新数据（与数据处理模块互通）
            WeakReferenceMessenger.Default.Send(new SampleSequenceChangedMessage(MeasurementSequence.ToList()));
            
            // 广播最完整的时序曲线给报告模块画图（包含所有测过的样品）
            var exportData = _plotCache.Select(kvp => new FlowInjectionReportData(kvp.Key.SampleName, kvp.Value)).ToList();
            WeakReferenceMessenger.Default.Send(new FlowInjectionDataExportMessage(exportData));

            CollectionProgressText = $"共切分出 {peakWindows.Count} 次有效峰形！扫描结束。";
        }

        private double CalculateGlobalBaseline(List<PlotPoint> currentPoints)
        {
            var sortedIntensities = currentPoints.Select(p => p.Intensity).OrderBy(i => i).ToList();
            double minInt = sortedIntensities.First();
            double maxInt = sortedIntensities.Last();
            int binCount = 50;
            double binSize = (maxInt - minInt) / binCount;
            if (binSize <= 0) binSize = 1;

            int[] histogram = new int[binCount];
            foreach (var p in currentPoints)
            {
                int bin = (int)((p.Intensity - minInt) / binSize);
                if (bin >= binCount) bin = binCount - 1;
                if (bin < 0) bin = 0;
                histogram[bin]++;
            }

            int[] smoothedHist = new int[binCount];
            for (int i = 0; i < binCount; i++)
            {
                smoothedHist[i] = histogram[i];
                if (i > 0) smoothedHist[i] += histogram[i - 1];
                if (i < binCount - 1) smoothedHist[i] += histogram[i + 1];
            }

            int thresholdCount = Math.Max(1, currentPoints.Count / 20); 
            var modes = new List<int>();
            for (int i = 0; i < binCount; i++)
            {
                if (smoothedHist[i] > thresholdCount)
                {
                    bool isLocalMax = true;
                    if (i > 0 && smoothedHist[i - 1] > smoothedHist[i]) isLocalMax = false;
                    if (i < binCount - 1 && smoothedHist[i + 1] > smoothedHist[i]) isLocalMax = false;
                    
                    if (isLocalMax && (modes.Count == 0 || modes.Last() != i - 1 || smoothedHist[modes.Last()] != smoothedHist[i]))
                    {
                        modes.Add(i);
                    }
                }
            }

            int baselineBin = 0;
            if (modes.Count > 0)
            {
                var validModes = modes.Where(m => (minInt + m * binSize) > 100).ToList();
                if (validModes.Count > 0)
                {
                    baselineBin = validModes.OrderBy(m => m).First();
                }
                else
                {
                    baselineBin = modes.OrderBy(m => m).First();
                }
            }
            else
            {
                int maxBin = 0;
                for (int i = 1; i < binCount; i++) if (smoothedHist[i] > smoothedHist[maxBin]) maxBin = i;
                baselineBin = maxBin;
            }

            return minInt + baselineBin * binSize + binSize / 2.0;
        }

        private void CalculateOverallAveragesAndRsds()
        {
            if (CurrentSample == null) return;
            foreach (var ec in CurrentSample.ElementConcentrations)
            {
                var validReps = ec.Reps.Where(r => r.Intensity.HasValue).Select(r => r.Intensity!.Value).ToList();
                if (validReps.Count > 0)
                {
                    double avg = validReps.Average();
                    ec.MeasuredIntensity = Math.Round(avg, 2);
                    
                    if (validReps.Count > 1 && avg > 0)
                    {
                        double sum = validReps.Sum(d => (d - avg) * (d - avg));
                        double stdDev = Math.Sqrt(sum / (validReps.Count - 1));
                        ec.MeasuredRsd = Math.Round((stdDev / avg) * 100, 2);
                    }
                    else
                    {
                        ec.MeasuredRsd = 0;
                    }
                }
            }
        }

        [RelayCommand]
        private void FinishSample()
        {
            if (CurrentSample == null) return;
            CurrentSample.Status = "已完成";
            
            // 自动保存至 JSON 磁盘
            _configService.SaveResults(MeasurementSequence.ToList());
            
            CollectionProgressText = "就绪 - 等待启动采集";

            int currentIndex = MeasurementSequence.IndexOf(CurrentSample);
            if (currentIndex < MeasurementSequence.Count - 1)
            {
                CurrentSample = MeasurementSequence[currentIndex + 1];
            }
            else
            {
                CollectionProgressText = "全部样品测量完毕！请点击【实验结束并处理数据】。";
            }
        }

        [RelayCommand]
        private void FinishExperimentAndNavigate()
        {
            MessageBox.Show("实验已结束，正在为您跳转至数据处理界面！", "实验结束", MessageBoxButton.OK, MessageBoxImage.Information);
            WeakReferenceMessenger.Default.Send(new NavigateMessage("DataProcessing"));
        }

        [RelayCommand]
        private void ClearCurrentSampleData()
        {
            if (CurrentSample == null)
            {
                MessageBox.Show("请先选择要清空数据的样品！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (IsScanning)
            {
                MessageBox.Show("正在扫描中，请先停止扫描后再清空！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"确定要清空样品【{CurrentSample.SampleName}】的全部测量数据吗？\n清空后该样品将恢复为“等待”状态，且所有流注分析记录将被删除。", "清空确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                CurrentSample.Status = "等待";
                foreach (var ec in CurrentSample.ElementConcentrations)
                {
                    ec.MeasuredIntensity = 0;
                    ec.MeasuredRsd = 0;
                    ec.Reps.Clear();
                }
                
                // 同时清空该样品对应的时序图缓存
                if (_plotCache.ContainsKey(CurrentSample))
                {
                    _plotCache.Remove(CurrentSample);
                }

                // 通知前端清空图表显示
                WeakReferenceMessenger.Default.Send(new FlowInjectionPlotBatchMessage(new Dictionary<string, List<PlotPoint>>()));
                
                // 广播更新序列表格
                WeakReferenceMessenger.Default.Send(new SampleSequenceChangedMessage(MeasurementSequence.ToList()));
                
                // 广播更新报告模块（移除该样品的图）
                var exportData = _plotCache.Select(kvp => new FlowInjectionReportData(kvp.Key.SampleName, kvp.Value)).ToList();
                WeakReferenceMessenger.Default.Send(new FlowInjectionDataExportMessage(exportData));
                
                MessageBox.Show("该样品的测量数据及历史时序图已成功清空！您可以随时重新开始扫描。", "已清空", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}