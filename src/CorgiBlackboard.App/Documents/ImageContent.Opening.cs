using System.Windows;
using CorgiBlackboard.App.Documents;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Infrastructure.Images;
using CorgiBlackboard.Infrastructure.Diagnostics;
using System.Diagnostics;
using Microsoft.Win32;

namespace CorgiBlackboard.App;

public partial class MainWindow
{
    private readonly CancellationTokenSource _imageLoads = new();
    private CancellationTokenSource? _loading;
    private readonly List<DocumentOpened> _imageOpenActions = [];
    public IReadOnlyList<DocumentOpened> ImageOpenActions => _imageOpenActions.AsReadOnly();
    public string? ImageLoadError { get; private set; }
    public event Action<string>? ImageLoadFailed;

    private void InitializeImages()
    {
        OpenImageRequested += PickImage;
        Documents.Opened += opened =>
        {
            _imageOpenActions.Add(opened);
            _diagnostics?.TryRecord(new DiagnosticEvent("DocumentOpened", DiagnosticKind.Asset,
                SurfaceId: opened.SurfaceId.Value.ToString("D"), WindowId: opened.DocumentId.Value.ToString("D"),
                Data: new Dictionary<string, string>
                {
                    ["documentId"] = opened.DocumentId.Value.ToString("D"),
                    ["assetId"] = opened.AssetId.Value.ToString("D"),
                    ["openOrder"] = opened.OpenOrder.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }));
        };
        Closed += (_, _) =>
        {
            _imageLoads.Cancel();
            _imageLoads.Dispose();
        };
    }

    private async void PickImage(object? sender, EventArgs args)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            _diagnostics?.TryRecord(new DiagnosticEvent("ImagePickerCancelled"));
            return;
        }
        try
        {
            await OpenImageAsync(dialog.FileName);
        }
        catch (ImageLoadException error)
        {
            ImageErrorText.Text = error.Message;
        }
        catch (OperationCanceledException) { }
    }

    public async Task<DocumentOpened> OpenImageAsync(string path, CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        _loading?.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _imageLoads.Token);
        _loading = linked;
        ImageLoadError = null;
        ImageErrorText.Text = "正在加载图片…";
        ImportStatus.Visibility = Visibility.Visible;
        CancelImportButton.Visibility = Visibility.Visible;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var image = await new ImageLoader().LoadAsync(path, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            // Validate the host before committing decoded resource identity.
            if (DocumentWindowLayer.ActualWidth <= 0 || DocumentWindowLayer.ActualHeight <= 0)
                throw new InvalidOperationException("The teaching area must be arranged before opening an image.");
            var asset = new Asset(AssetId.New(), image.MediaType, image.FileLength);
            Workspace.AddAsset(asset);
            var document = Workspace.AddImageDocument(asset.Id, image.Pixels.PixelWidth, image.Pixels.PixelHeight);
            var content = new ImageContent(Editing, document, image.Pixels, this);
            var opened = Documents.Open(document, System.IO.Path.GetFileName(path), content, content);
            _diagnostics?.TryRecord(DiagnosticProducers.ImageLoaded(
                new DiagnosticAsset(System.IO.Path.GetFileName(path), image.MediaType, image.FileLength, image.Sha256),
                Stopwatch.GetElapsedTime(started)) with
                {
                    SurfaceId = opened.SurfaceId.Value.ToString("D"), WindowId = opened.DocumentId.Value.ToString("D"),
                    Data = new Dictionary<string, string>
                    {
                        ["pixelWidth"] = image.Pixels.PixelWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["pixelHeight"] = image.Pixels.PixelHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                });
            ImportStatus.Visibility = Visibility.Collapsed;
            return opened;
        }
        catch (ImageLoadException error)
        {
            _diagnostics?.TryRecord(DiagnosticProducers.Failure("ImageLoadFailed", error, Stopwatch.GetElapsedTime(started)));
            if (ReferenceEquals(_loading, linked))
            {
                ImageLoadError = error.Message;
                ImageErrorText.Text = error.Message;
                ImageLoadFailed?.Invoke(error.Message);
            }
            throw;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            _diagnostics?.TryRecord(new DiagnosticEvent("ImageLoadCancelled",
                DurationMilliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            if (ReferenceEquals(_loading, linked))
                ImageErrorText.Text = "已取消图片加载。";
            throw;
        }
        finally
        {
            if (ReferenceEquals(_loading, linked))
            {
                _loading = null;
                CancelImportButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CancelImport(object sender, RoutedEventArgs args) => _loading?.Cancel();
}
