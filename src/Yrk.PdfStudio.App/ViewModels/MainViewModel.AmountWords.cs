using CommunityToolkit.Mvvm.Input;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>The amount in words window: the same words as the right-click menu, without having to find it.</summary>
public sealed partial class MainViewModel
{
    private IRelayCommand? _amountWordsCommand;

    public IRelayCommand AmountWordsCommand => _amountWordsCommand ??= new RelayCommand(AskAmountWords);

    private void AskAmountWords()
    {
        if (_dialogs.AskAmountWords(_document is not null) is { } words)
        {
            BeginPlacement(new Placement(PlacedItemKind.Text, null, null, "Click where the words should go. Press Esc to cancel.", Words: words));
        }
    }
}
