using System.Windows;
using System.Windows.Controls;
using GitManager.Services.Repositories;

namespace GitManager.Views;

public partial class ReadmeView : UserControl
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(nameof(Document), typeof(ReadmeDocument), typeof(ReadmeView), new PropertyMetadata(null, (owner, _) => ((ReadmeView)owner).Render()));
    public ReadmeDocument? Document { get => (ReadmeDocument?)GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(ReadmeView), new PropertyMetadata(false, (owner, _) => ((ReadmeView)owner).Render()));
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    private ReadmeDocument? _rendered;

    public ReadmeView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Render();
    }

    // 隐藏时不排版，切到说明页时才生成文档，避免切换仓库时做无用功。
    private void Render()
    {
        EmptyText.Visibility = Document is null && !IsLoading ? Visibility.Visible : Visibility.Collapsed;
        if (!IsVisible || Equals(_rendered, Document)) return;
        _rendered = Document;
        DocumentViewer.Document = Document is null ? null : MarkdownDocument.Build(Document.Text);
    }
}
