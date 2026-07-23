using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ScottPlot;
using System.Drawing; // Because ImageFormat might be from System.Drawing.Imaging or ScottPlot
using System.IO;
using System;

namespace GD_ControlCenter_WPF.ViewModels
{
    public class ReportCalibrationRow
    {
        public string ElementName { get; set; } = string.Empty;
        public string Equation { get; set; } = string.Empty;
        public string R2 { get; set; } = string.Empty;
        public BitmapImage? GraphImage { get; set; }
    }

    public class ReportSampleRow
    {
        public string SampleName { get; set; } = string.Empty;
        public string ElementName { get; set; } = string.Empty;
        public double Intensity { get; set; }
        public double RSD { get; set; }
        public string CalculatedConc { get; set; } = string.Empty;
    }

    public class ReportFlowInjectionGraph
    {
        public string SampleName { get; set; } = string.Empty;
        public BitmapImage? GraphImage { get; set; }
    }

    public partial class ReportGenerationViewModel : ObservableObject
    {
        private List<SampleItemModel> _rawFullSequence = new();

        [ObservableProperty] private ObservableCollection<string> _reportTemplates = new(new[] { "标准检测报告 (默认)", "简易内部报告" });
        [ObservableProperty] private string _selectedTemplate = "标准检测报告 (默认)";

        [ObservableProperty] private string _operatorName = "管理员";
        [ObservableProperty] private string _reportNote = "";
        [ObservableProperty] private string _reportTitle = "实验分析报告";

        [ObservableProperty] private bool _includeCalibrationCurves = true;
        [ObservableProperty] private bool _includeStandardData = false;
        [ObservableProperty] private bool _includeSampleResults = true;
        [ObservableProperty] private bool _includeRsdAnalysis = true;
        [ObservableProperty] private bool _includeHardwareStatus = false;

        [ObservableProperty] private string _reportDate = DateTime.Now.ToString("yyyy-MM-dd");

        // 供 UI 绑定用于动态生成的集合
        [ObservableProperty] private ObservableCollection<ReportCalibrationRow> _calibrationData = new();
        [ObservableProperty] private ObservableCollection<ReportSampleRow> _sampleData = new();
        
        [ObservableProperty] private ObservableCollection<ReportFlowInjectionGraph> _flowInjectionGraphs = new();
        public bool HasFlowInjectionGraphs => FlowInjectionGraphs.Count > 0;

        private List<FlowInjectionReportData> _latestFlowInjectionData = new();

        // 用于将后台生成的 FlowDocument 传给 View（因为 View 需要将它喂给 DocumentViewer 或进行 Print）
        public Action<FlowDocument>? OnPreviewReady;
        public Action? OnPrintRequested;

        public ReportGenerationViewModel()
        {
            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) =>
            {
                _rawFullSequence = m.Value;
                
                // 任何新的序列变动（无论是连续进样还是流动注射），都先清空旧的时序图残留。
                // 如果是流动注射模式，紧接着发出的 FlowInjectionDataExportMessage 会立刻将其重新填满。
                // 这样彻底杜绝了“同名样品连续进样时带入上次流注图谱”的幽灵残留 Bug。
                if (_latestFlowInjectionData != null)
                {
                    _latestFlowInjectionData.Clear();
                }
            });

            WeakReferenceMessenger.Default.Register<FlowInjectionDataExportMessage>(this, (r, m) =>
            {
                _latestFlowInjectionData = m.Value;
            });
        }

        private BitmapImage LoadImageFromBytes(byte[] imageData)
        {
            var image = new BitmapImage();
            using (var mem = new System.IO.MemoryStream(imageData))
            {
                mem.Position = 0;
                image.BeginInit();
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = null;
                image.StreamSource = mem;
                image.EndInit();
            }
            image.Freeze(); // 允许跨线程绑定
            return image;
        }

        private void GenerateReportData()
        {
            CalibrationData.Clear();
            SampleData.Clear();

            if (_rawFullSequence == null || _rawFullSequence.Count == 0) return;

            // 1. 提取出所有出现过的元素
            var allElements = _rawFullSequence
                .SelectMany(s => s.ElementConcentrations)
                .Select(e => e.ElementName)
                .Distinct()
                .ToList();

            // 2. 为每个元素分别计算拟合曲线
            var elementEquations = new Dictionary<string, (double Slope, double Intercept)>();

            foreach (var element in allElements)
            {
                // 提取该元素的所有标准点（标液、空白）
                var standardPoints = _rawFullSequence
                    .Where(s => s.Type == SampleType.标液 || s.Type == SampleType.空白)
                    .Select(s => {
                        var target = s.ElementConcentrations.FirstOrDefault(e => e.ElementName == element);
                        double conc = (s.Type == SampleType.空白) ? 0 :
                                     (target != null && double.TryParse(target.ConcentrationValue, out var d) ? d : 0);
                        return new { Conc = conc, Intensity = target?.MeasuredIntensity ?? 0 };
                    })
                    .ToList();

                int n = standardPoints.Count;
                if (n >= 2)
                {
                    double sumX = standardPoints.Sum(p => p.Conc);
                    double sumY = standardPoints.Sum(p => p.Intensity);
                    double sumXY = standardPoints.Sum(p => p.Conc * p.Intensity);
                    double sumX2 = standardPoints.Sum(p => p.Conc * p.Conc);

                    double denominator = (n * sumX2 - sumX * sumX);
                    if (Math.Abs(denominator) > 1e-10)
                    {
                        double slope = (n * sumXY - sumX * sumY) / denominator;
                        double intercept = (sumY - slope * sumX) / n;

                        double yAvg = sumY / n;
                        double ssRes = standardPoints.Sum(p => Math.Pow(p.Intensity - (slope * p.Conc + intercept), 2));
                        double ssTot = standardPoints.Sum(p => Math.Pow(p.Intensity - yAvg, 2));
                        double r2 = (ssTot == 0) ? 1 : 1 - (ssRes / ssTot);

                        elementEquations[element] = (slope, intercept);

                        // --- 使用 ScottPlot 在后台渲染校准曲线图 ---
                        var plt = new ScottPlot.Plot();
                        plt.XLabel("浓度");
                        plt.YLabel("强度");
                        plt.Title($"{element} 校准曲线");
                        
                        var xs = standardPoints.Select(p => p.Conc).ToArray();
                        var ys = standardPoints.Select(p => p.Intensity).ToArray();
                        plt.Add.Scatter(xs, ys);
                        
                        // 画拟合线
                        double minX = xs.Min();
                        double maxX = xs.Max();
                        // 稍微延伸一点
                        double padX = (maxX - minX) * 0.1;
                        if (padX == 0) padX = 1;
                        var lineXs = new double[] { minX - padX, maxX + padX };
                        var lineYs = lineXs.Select(x => slope * x + intercept).ToArray();
                        var line = plt.Add.ScatterLine(lineXs, lineYs);
                        // line.LineStyle.Pattern = LinePattern.Dashes; // Remove to fix CS0117

                        byte[] imgBytes = plt.GetImageBytes(300, 200, ImageFormat.Png);
                        var graphImg = LoadImageFromBytes(imgBytes);

                        CalibrationData.Add(new ReportCalibrationRow
                        {
                            ElementName = element,
                            Equation = $"y = {slope:F4}x + ({intercept:F4})",
                            R2 = r2.ToString("F4"),
                            GraphImage = graphImg
                        });
                    }
                    else
                    {
                        CalibrationData.Add(new ReportCalibrationRow { ElementName = element, Equation = "共线无效", R2 = "0" });
                    }
                }
                else
                {
                    CalibrationData.Add(new ReportCalibrationRow { ElementName = element, Equation = "标点不足", R2 = "0" });
                }
            }

            // 3. 处理待测液
            var sampleRows = new List<ReportSampleRow>();
            foreach (var sample in _rawFullSequence.Where(s => s.Type == SampleType.待测液))
            {
                foreach (var element in allElements)
                {
                    var target = sample.ElementConcentrations.FirstOrDefault(e => e.ElementName == element);
                    if (target != null)
                    {
                        string concStr = "N/A";
                        if (elementEquations.TryGetValue(element, out var eq) && eq.Slope != 0)
                        {
                            double conc = (target.MeasuredIntensity - eq.Intercept) / eq.Slope;
                            concStr = conc.ToString("F3");
                        }

                        sampleRows.Add(new ReportSampleRow
                        {
                            SampleName = sample.SampleName,
                            ElementName = element,
                            Intensity = target.MeasuredIntensity,
                            RSD = target.MeasuredRsd,
                            CalculatedConc = concStr
                        });
                    }
                }
            }

            // 按样品名排序插入
            foreach (var row in sampleRows.OrderBy(r => r.SampleName))
            {
                SampleData.Add(row);
            }

            // 4. 渲染流动注射时序总图（支持多个样品）
            FlowInjectionGraphs.Clear();
            if (_latestFlowInjectionData != null && _latestFlowInjectionData.Count > 0)
            {
                foreach (var reportData in _latestFlowInjectionData)
                {
                    string sampleName = reportData.SampleName;
                    var elementsData = reportData.ElementData;

                    var plt = new ScottPlot.Plot();
                    plt.XLabel("时间 (s)");
                    plt.YLabel("绝对发光强度");
                    plt.Title($"流动注射全程时序曲线 - {sampleName}");
                    plt.ShowLegend();

                    bool hasData = false;
                    foreach (var kvp in elementsData)
                    {
                        if (kvp.Value.Count < 2) continue;
                        var xs = kvp.Value.Select(p => p.Time).ToArray();
                        var ys = kvp.Value.Select(p => p.Intensity).ToArray();
                        var sp = plt.Add.ScatterLine(xs, ys);
                        sp.LegendText = kvp.Key;
                        hasData = true;
                    }

                    if (hasData)
                    {
                        byte[] imgBytes = plt.GetImageBytes(600, 300, ImageFormat.Png);
                        FlowInjectionGraphs.Add(new ReportFlowInjectionGraph 
                        {
                            SampleName = sampleName,
                            GraphImage = LoadImageFromBytes(imgBytes)
                        });
                    }
                }
            }
            OnPropertyChanged(nameof(HasFlowInjectionGraphs));
        }
        [RelayCommand]
        private void PreviewReport()
        {
            GenerateReportData();
            // 通知 View 刷新预览或者生成新的 FlowDocument
            // 我们可以在 View 里面用 ItemsControl 绑定 CalibrationData 和 SampleData，这样更符合 WPF 的方式。
            // 不必在 ViewModel 手拼 FlowDocument。
        }

        [RelayCommand]
        private void ExportPdf()
        {
            // 点击导出 PDF 时，我们只需让 View 弹出 PrintDialog 打印当前的预览区即可。
            OnPrintRequested?.Invoke();
        }
    }
}
