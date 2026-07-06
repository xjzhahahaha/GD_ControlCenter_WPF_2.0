using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;

namespace GD_ControlCenter_WPF.ViewModels
{
    public class ReportCalibrationRow
    {
        public string ElementName { get; set; } = string.Empty;
        public string Equation { get; set; } = string.Empty;
        public string R2 { get; set; } = string.Empty;
    }

    public class ReportSampleRow
    {
        public string SampleName { get; set; } = string.Empty;
        public string ElementName { get; set; } = string.Empty;
        public double Intensity { get; set; }
        public double RSD { get; set; }
        public string CalculatedConc { get; set; } = string.Empty;
    }

    public partial class ReportGenerationViewModel : ObservableObject
    {
        private List<SampleItemModel> _rawFullSequence = new();

        [ObservableProperty] private ObservableCollection<string> _reportTemplates = new(new[] { "标准检测报告 (默认)", "简易内部报告" });
        [ObservableProperty] private string _selectedTemplate = "标准检测报告 (默认)";

        [ObservableProperty] private string _operatorName = "管理员";
        [ObservableProperty] private string _reportNote = "";

        [ObservableProperty] private bool _includeCalibrationCurves = true;
        [ObservableProperty] private bool _includeStandardData = false;
        [ObservableProperty] private bool _includeSampleResults = true;
        [ObservableProperty] private bool _includeRsdAnalysis = true;
        [ObservableProperty] private bool _includeHardwareStatus = false;

        [ObservableProperty] private string _reportDate = DateTime.Now.ToString("yyyy-MM-dd");

        // 供 UI 绑定用于动态生成的集合
        [ObservableProperty] private ObservableCollection<ReportCalibrationRow> _calibrationData = new();
        [ObservableProperty] private ObservableCollection<ReportSampleRow> _sampleData = new();

        // 用于将后台生成的 FlowDocument 传给 View（因为 View 需要将它喂给 DocumentViewer 或进行 Print）
        public Action<FlowDocument>? OnPreviewReady;
        public Action? OnPrintRequested;

        public ReportGenerationViewModel()
        {
            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) =>
            {
                _rawFullSequence = m.Value;
                // 数据发生变化时，自动刷新数据
                GenerateReportData();
            });
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

                        CalibrationData.Add(new ReportCalibrationRow
                        {
                            ElementName = element,
                            Equation = $"y = {slope:F4}x + ({intercept:F4})",
                            R2 = r2.ToString("F4")
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
