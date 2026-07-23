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
        private readonly Dictionary<string, ScottPlot.Plottables.Scatter> _scatters = new();
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
                        _scatters.Clear();
                        TimeSeriesPlot.Refresh();
                    });
                }
            });

            // 订阅：时序图坐标点刷新 (多元素并发)
            WeakReferenceMessenger.Default.Register<FlowInjectionPlotMessage>(this, (r, m) =>
            {
                Dispatcher.BeginInvoke(() => RenderTimeSeriesPoint(m.Value));
            });

            // 订阅：批量渲染完整时序图 (切换样品时恢复历史图表)
            WeakReferenceMessenger.Default.Register<FlowInjectionPlotBatchMessage>(this, (r, m) =>
            {
                Dispatcher.BeginInvoke(() => RenderTimeSeriesBatch(m.Value));
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
            if (!_scatters.ContainsKey(pt.ElementName))
            {
                // 创建一个初始包含这个新点的 ScatterLine
                double[] xs = new[] { pt.Time };
                double[] ys = new[] { pt.Intensity };
                var scatter = TimeSeriesPlot.Plot.Add.Scatter(xs, ys);
                scatter.LegendText = pt.ElementName;
                scatter.Color = _palette[_scatters.Count % _palette.Length];
                scatter.LineWidth = 2;
                scatter.MarkerSize = 0; // 实时刷新为了性能不画圆点
                _scatters[pt.ElementName] = scatter;
                TimeSeriesPlot.Plot.ShowLegend();
            }
            else
            {
                var scatter = _scatters[pt.ElementName];
                var oldXs = scatter.Data.GetScatterPoints().Select(p => p.X).ToList();
                var oldYs = scatter.Data.GetScatterPoints().Select(p => p.Y).ToList();
                oldXs.Add(pt.Time);
                oldYs.Add(pt.Intensity);
                
                // 移除旧的曲线，添加新的曲线
                TimeSeriesPlot.Plot.Remove(scatter);
                
                var newScatter = TimeSeriesPlot.Plot.Add.Scatter(oldXs.ToArray(), oldYs.ToArray());
                newScatter.LegendText = pt.ElementName;
                newScatter.Color = _palette[(_scatters.Keys.ToList().IndexOf(pt.ElementName)) % _palette.Length];
                newScatter.LineWidth = 2;
                newScatter.MarkerSize = 0;
                
                _scatters[pt.ElementName] = newScatter;
            }
            
            // 手动调整缩放，避免老数据被系统自动截断
            if (_scatters.Count > 0)
            {
                TimeSeriesPlot.Plot.Axes.AutoScale();
                TimeSeriesPlot.Refresh();
            }
        }

        private void RenderTimeSeriesBatch(Dictionary<string, List<PlotPoint>> plotData)
        {
            TimeSeriesPlot.Plot.Clear();
            _scatters.Clear();

            foreach (var kvp in plotData)
            {
                var elementName = kvp.Key;
                var points = kvp.Value;
                if (points == null || points.Count == 0) continue;

                var xs = points.Select(p => p.Time).ToArray();
                var ys = points.Select(p => p.Intensity).ToArray();

                var scatter = TimeSeriesPlot.Plot.Add.Scatter(xs, ys);
                scatter.LegendText = elementName;
                scatter.Color = _palette[_scatters.Count % _palette.Length];
                scatter.LineWidth = 2;
                scatter.MarkerSize = 0;
                _scatters[elementName] = scatter;
            }

            if (_scatters.Count > 0)
            {
                TimeSeriesPlot.Plot.ShowLegend();
                TimeSeriesPlot.Plot.Axes.AutoScale();
            }
            TimeSeriesPlot.Refresh();
        }
    }
}
