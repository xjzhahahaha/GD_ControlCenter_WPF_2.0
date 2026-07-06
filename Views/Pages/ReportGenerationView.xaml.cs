using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using GD_ControlCenter_WPF.ViewModels;

namespace GD_ControlCenter_WPF.Views.Pages
{
    public partial class ReportGenerationView : UserControl
    {
        public ReportGenerationView()
        {
            InitializeComponent();

            this.DataContextChanged += (s, e) =>
            {
                if (this.DataContext is ReportGenerationViewModel vm)
                {
                    vm.OnPrintRequested = PrintReport;
                }
            };
        }

        private void PrintReport()
        {
            if (ReportDocument == null)
            {
                MessageBox.Show("报表尚未生成或不可用！");
                return;
            }

            PrintDialog printDialog = new PrintDialog();
            if (printDialog.ShowDialog() == true)
            {
                // 设置页面边距和大小
                ReportDocument.PageHeight = printDialog.PrintableAreaHeight;
                ReportDocument.PageWidth = printDialog.PrintableAreaWidth;
                ReportDocument.PagePadding = new Thickness(50);
                ReportDocument.ColumnGap = 0;
                ReportDocument.ColumnWidth = printDialog.PrintableAreaWidth;

                // 强制重排版
                IDocumentPaginatorSource idpSource = ReportDocument;
                
                try
                {
                    printDialog.PrintDocument(idpSource.DocumentPaginator, "实验分析报告");
                    MessageBox.Show("报告导出/打印成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}");
                }
            }
        }
    }
}
