using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Views;

/// <summary>
/// Creates a signature: drawn (mouse, pen with pressure, or touch) or typed become vector outlines;
/// imported SVG and PDF stay vector, PNG and JPEG stay images.
/// </summary>
public partial class SignatureDialog : Window
{
    private static readonly Color Blue = Color.FromRgb(0x1D, 0x3F, 0x9A);

    private readonly Func<byte[], Task<SignatureArt?>> _preparePdf;
    private SignatureArt? _imported;

    public SignatureDialog(Func<byte[], Task<SignatureArt?>> preparePdf)
    {
        _preparePdf = preparePdf;
        InitializeComponent();
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Black,
            Width = 2.6,
            Height = 2.6,
            FitToCurve = true,
            IgnorePressure = false,
        };

        FontList.ItemsSource = SignatureImaging.AvailableTypedFonts();
        FontList.SelectedIndex = 0;
    }

    /// <summary>The signature; set when the dialog returns true.</summary>
    public SignatureArt? Signature { get; private set; }

    public bool Remember => RememberBox.IsChecked == true;

    public bool LockWithDigitalId => LockBox.IsChecked == true;

    private Color InkColor => BlueInk?.IsChecked == true ? Blue : Colors.Black;

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e) => UpdateUseButton();

    private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateUseButton();

    private void OnInkColorChanged(object sender, RoutedEventArgs e)
    {
        if (Ink is null || BlueInk is null)
        {
            return; // Raised by IsChecked="True" while the window is still being built.
        }

        Ink.DefaultDrawingAttributes.Color = InkColor;
        foreach (var stroke in Ink.Strokes)
        {
            stroke.DrawingAttributes.Color = InkColor;
        }
    }

    private void OnClearInk(object sender, RoutedEventArgs e)
    {
        Ink.Strokes.Clear();
        UpdateUseButton();
    }

    private async void OnChooseImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Choose a signature file"),
            Filter = Loc.T("Signatures (*.png;*.jpg;*.jpeg;*.bmp;*.svg;*.pdf)|*.png;*.jpg;*.jpeg;*.bmp;*.svg;*.pdf"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var path = dialog.FileName;
            _imported = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".svg" => SignatureArt.FromVector(SvgImport.Load(path)),
                ".pdf" => await _preparePdf(await File.ReadAllBytesAsync(path))
                    ?? throw new FormatException(Loc.T("The PDF has no signature on its first page.")),
                _ => SignatureArt.FromImage(SignatureImaging.FromImageFile(path)),
            };
            ImportedPreview.Source = _imported.Preview;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or FormatException or PdfException)
        {
            MessageBox.Show(this, Loc.F("The file could not be used as a signature.\n\n{0}", Loc.T(ex.Message)), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateUseButton();
    }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        Signature = CreateSignature();
        if (Signature is not null)
        {
            DialogResult = true;
        }
    }

    private SignatureArt? CreateSignature() => Modes.SelectedIndex switch
    {
        0 => VectorArtBuilder.FromStrokes(Ink.Strokes) is { } drawn ? SignatureArt.FromVector(drawn) : null,
        1 => FontList.SelectedItem is FontFamily font && VectorArtBuilder.FromText(TypedName.Text, font, InkColor) is { } typed
            ? SignatureArt.FromVector(typed)
            : null,
        _ => _imported,
    };

    private void UpdateUseButton()
    {
        if (UseButton is null)
        {
            return;
        }

        UseButton.IsEnabled = Modes.SelectedIndex switch
        {
            0 => Ink.Strokes.Count > 0,
            1 => TypedName.Text.Trim().Length > 0 && FontList.SelectedItem is not null,
            _ => _imported is not null,
        };
    }
}
