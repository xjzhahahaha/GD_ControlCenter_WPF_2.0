using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.ViewModels;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using ScottPlot;
using System.Windows.Controls;
using System;
using System.Collections.Generic;

namespace GD_ControlCenter_WPF.Views.Pages
{
    public partial class FlowInjectionView : UserControl
    {
        private readonly Dictionary<string, ScottPlot.Plottables.DataLogger> _dataLoggers = new();
        private readonly Color[] _palette = new[] { Colors.Red, Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple, Colors.Magenta, Colors.Cyan, Colors.Olive };

        public FlowInjectionView()
        {
            InitializeComponent();
            
            // X轴为时间秒数，Y轴为强度
            TimeSeriesPlot.Plot.XLabel("时间 (秒)");
            TimeSeriesPlot.Plot.YLabel("发光强度");

            // 订阅：全谱图刷新
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                Dispatcher.BeginInvoke(() => RenderFullSpectrum(m.Value));
            });

            // 订阅：清空多元素时序图
            WeakReferenceMessenger.Default.Register<SwitchPlotElementMessage>(this, (r, m) =>
            {
                if (m.Value == "CLEAR_ALL")
                {
                    Dispatcher.Invoke(() => 
                    {
                        TimeSeriesPlot.Plot.Clear();
                        _dataLoggers.Clear();
                        TimeSeriesPlot.Refresh();
                    });
                }
            });

            // 订阅：时序图坐标点刷新 (多元素并发)
            WeakReferenceMessenger.Default.Register<FlowInjectionPlotMessage>(this, (r, m) =>
            {
                Dispatcher.BeginInvoke(() => RenderTimeSeriesPoint(m.Value));
            });
        }

        private void RenderFullSpectrum(SpectralData data)
        {
            if (data.Wavelengths == null || data.Wavelengths.Length == 0) return;

            SpecPlot.Plot.Clear();
            var fullLine = SpecPlot.Plot.Add.Scatter(data.Wavelengths, data.Intensities);
            fullLine.MarkerSize = 0;
            fullLine.Color = ScottPlot.Colors.MediumPurple;
            SpecPlot.Plot.Axes.AutoScale();
            SpecPlot.Refresh();
        }

        private void RenderTimeSeriesPoint(PlotPoint pt)
        {
            if (!_dataLoggers.ContainsKey(pt.ElementName))
            {
                var logger = TimeSeriesPlot.Plot.Add.DataLogger();
                logger.LegendText = pt.ElementName;
                logger.Color = _palette[_dataLoggers.Count % _palette.Length];
                logger.LineWidth = 2;
                _dataLoggers[pt.ElementName] = logger;
                TimeSeriesPlot.Plot.ShowLegend();
            }

            _dataLoggers[pt.ElementName].Add(pt.Time, pt.Intensity);
            
            // 为了避免频繁触发全局重绘导致卡顿，我们让 ScottPlot 自己管理范围
            if (_dataLoggers.Count > 0)
            {
                TimeSeriesPlot.Plot.Axes.AutoScale();
                TimeSeriesPlot.Refresh();
            }
        }
    }
}
