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
                    timeOffset = _scanBuffers.Values.First().Last().Time;
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

            int currentRepIndex = (CurrentSample.ElementConcentrations.Max(e => (int?)e.Reps.Count) ?? 0) + 1;
            
            foreach (var conf in _elementConfigVM.SelectedConfigs)
            {
                string key = $"{conf.ElementName}({conf.Wavelength})";
                if (!_scanBuffers.ContainsKey(key)) continue;

                var dataPoints = _scanBuffers[key];
                var currentPoints = dataPoints.Skip(_currentScanPointStartIndex).ToList();
                if (currentPoints.Count < 3) continue;
                
                // --- 自动寻峰算法 ---
                // 1. 寻找本次注射中的绝对最大值作为峰顶
                var maxPoint = currentPoints.OrderByDescending(p => p.Intensity).First();
                int maxIndex = dataPoints.LastIndexOf(maxPoint);
                
                // 2. 估计基线水平 (取本次所有点中最低的 5% 的均值作为基线参考)
                var sortedIntensities = currentPoints.Select(p => p.Intensity).OrderBy(i => i).ToList();
                int baselineCount = Math.Max(1, sortedIntensities.Count / 20); 
                double globalBaseline = sortedIntensities.Take(baselineCount).Average();

                // 3. 定义起落峰阈值：基线之上 + 峰高(相对于基线)的 5%
                double peakHeightRough = maxPoint.Intensity - globalBaseline;
                double threshold = globalBaseline + peakHeightRough * 0.05;

                // 4. 从峰顶向左寻找起峰点（注意不要越界到上一次注射的数据）
                int startIndex = maxIndex;
                while (startIndex > _currentScanPointStartIndex && dataPoints[startIndex].Intensity > threshold)
                {
                    startIndex--;
                }

                // 5. 从峰顶向右寻找落峰点
                int endIndex = maxIndex;
                while (endIndex < dataPoints.Count - 1 && dataPoints[endIndex].Intensity > threshold)
                {
                    endIndex++;
                }

                // 6. 确定该窗口内的最终局部基线 (起终点的连线或直接取均值，这里简化为两端最小值)
                double localBaseline = Math.Min(dataPoints[startIndex].Intensity, dataPoints[endIndex].Intensity);
                
                // 7. 计算精准峰高和梯形积分面积
                double exactPeakHeight = maxPoint.Intensity - localBaseline;
                double peakArea = 0;
                for (int i = startIndex; i < endIndex; i++)
                {
                    double dt = dataPoints[i + 1].Time - dataPoints[i].Time;
                    double y1 = Math.Max(0, dataPoints[i].Intensity - localBaseline);
                    double y2 = Math.Max(0, dataPoints[i + 1].Intensity - localBaseline);
                    peakArea += (y1 + y2) * dt / 2.0;
                }

                // 注意：此处不再维护单次峰值明细 ScanRecords，改为由 ElementConcentrations 汇总展示

                // --- 将 PeakIntensity (峰高) 存入 CurrentSample 的浓度模型中作为主流测量值 ---
                // 注：当出现平顶峰（稳态）时，使用峰高代表浓度比面积更准确
                var targetRow = CurrentSample.ElementConcentrations.FirstOrDefault(e => e.ElementName == key || e.ElementName == conf.ElementName);
                if (targetRow != null)
                {
                    Application.Current.Dispatcher.Invoke(() => 
                    {
                        targetRow.Reps.Add(new MeasurementRepModel 
                        { 
                            RepIndex = currentRepIndex, 
                            Intensity = Math.Round(exactPeakHeight, 2), 
                            IsMeasuring = false 
                        });
                    });
                }
            }

            // 更新总体平均值和 RSD
            CalculateOverallAveragesAndRsds();
            
            // 向外广播最新数据（与数据处理模块互通）
            WeakReferenceMessenger.Default.Send(new SampleSequenceChangedMessage(MeasurementSequence.ToList()));
            
            // 广播最完整的时序曲线给报告模块画图（包含所有测过的样品）
            var exportData = new Dictionary<string, Dictionary<string, List<PlotPoint>>>();
            foreach (var kvp in _plotCache)
            {
                exportData[kvp.Key.SampleName] = kvp.Value;
            }
            WeakReferenceMessenger.Default.Send(new FlowInjectionDataExportMessage(exportData));

            CollectionProgressText = $"第 {currentRepIndex} 次注射记录完成！等待下一次扫描或切至下一瓶。";
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
                
                // 广播更新
                WeakReferenceMessenger.Default.Send(new SampleSequenceChangedMessage(MeasurementSequence.ToList()));
                
                MessageBox.Show("该样品的测量数据已成功清空！您可以随时重新开始扫描。", "已清空", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}