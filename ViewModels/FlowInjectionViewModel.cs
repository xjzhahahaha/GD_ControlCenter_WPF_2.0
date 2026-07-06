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

        // 历史扫描记录表
        [ObservableProperty] private ObservableCollection<FlowInjectionResultData> _scanRecords = new();

        [ObservableProperty] private ObservableCollection<string> _pickedElements = new();

        // 核心缓冲区：记录本次扫描的各元素时间序列，Key: "Element(Wavelength)"
        private readonly Dictionary<string, List<PlotPoint>> _scanBuffers = new();
        private DateTime _scanStartTime;

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

            // 清理缓冲区并通知 View 准备新画板
            _scanBuffers.Clear();
            foreach (var el in PickedElements)
            {
                _scanBuffers[el] = new List<PlotPoint>();
            }
            WeakReferenceMessenger.Default.Send(new SwitchPlotElementMessage("CLEAR_ALL"));

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
                CollectionProgressText = "硬件已就绪，正在进行流动注射时序采样，请注射溶液...";
                
                _scanStartTime = DateTime.Now;

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

            int currentRepIndex = (CurrentSample.ElementConcentrations.FirstOrDefault()?.Reps.Count ?? 0) + 1;
            
            foreach (var conf in _elementConfigVM.SelectedConfigs)
            {
                string key = $"{conf.ElementName}({conf.Wavelength})";
                if (!_scanBuffers.ContainsKey(key) || _scanBuffers[key].Count < 3) continue;

                var dataPoints = _scanBuffers[key];
                
                // --- 自动寻峰算法 ---
                // 1. 寻找绝对最大值作为峰顶
                var maxPoint = dataPoints.OrderByDescending(p => p.Intensity).First();
                int maxIndex = dataPoints.IndexOf(maxPoint);
                
                // 2. 估计基线水平 (取所有点中最低的 5% 的均值作为基线参考)
                var sortedIntensities = dataPoints.Select(p => p.Intensity).OrderBy(i => i).ToList();
                int baselineCount = Math.Max(1, sortedIntensities.Count / 20); 
                double globalBaseline = sortedIntensities.Take(baselineCount).Average();

                // 3. 定义起落峰阈值：基线之上 + 峰高(相对于基线)的 5%
                double peakHeightRough = maxPoint.Intensity - globalBaseline;
                double threshold = globalBaseline + peakHeightRough * 0.05;

                // 4. 从峰顶向左寻找起峰点
                int startIndex = maxIndex;
                while (startIndex > 0 && dataPoints[startIndex].Intensity > threshold)
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

                // --- 组装并展示分析结果 ---
                var resultData = new FlowInjectionResultData
                {
                    ScanIndex = currentRepIndex,
                    ElementName = key,
                    PeakIntensity = Math.Round(exactPeakHeight, 2),
                    BackgroundIntensity = Math.Round(localBaseline, 2),
                    StartTime = Math.Round(dataPoints[startIndex].Time, 2),
                    EndTime = Math.Round(dataPoints[endIndex].Time, 2),
                    PeakArea = Math.Round(peakArea, 2)
                };
                
                // 必须在 UI 线程操作 ObservableCollection
                Application.Current.Dispatcher.Invoke(() => ScanRecords.Add(resultData));

                // --- 将 PeakArea 存入 CurrentSample 的浓度模型中作为主流测量值 ---
                var targetRow = CurrentSample.ElementConcentrations.FirstOrDefault(e => e.ElementName == key || e.ElementName == conf.ElementName);
                if (targetRow != null)
                {
                    Application.Current.Dispatcher.Invoke(() => 
                    {
                        targetRow.Reps.Add(new MeasurementRepModel 
                        { 
                            RepIndex = currentRepIndex, 
                            Intensity = Math.Round(peakArea, 2), 
                            IsMeasuring = false 
                        });
                    });
                }
            }

            // 更新总体平均值和 RSD
            CalculateOverallAveragesAndRsds();
            
            // 向外广播最新数据（与数据处理模块互通）
            WeakReferenceMessenger.Default.Send(new SampleSequenceChangedMessage(MeasurementSequence.ToList()));
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
            
            ScanRecords.Clear(); 
            CollectionProgressText = "就绪 - 等待启动采集";

            int currentIndex = MeasurementSequence.IndexOf(CurrentSample);
            if (currentIndex < MeasurementSequence.Count - 1)
            {
                CurrentSample = MeasurementSequence[currentIndex + 1];
            }
            else
            {
                MessageBox.Show("全序列流动注射测量完成！数据已同步至数据处理模块。", "任务结束", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}