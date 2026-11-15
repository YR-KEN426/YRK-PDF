using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Stable error codes and English keys for the existing translation dictionary.</summary>
public static class ErrorCatalog
{
    public static ErrorInfo AppDispatcherUnhandled { get; } = new("YRK-APP-001",
        "Something went wrong while using the app.",
        "Try again. If the problem continues, copy the problem report and contact support.");

    public static ErrorInfo AppDomainUnhandled { get; } = new("YRK-APP-002",
        "The app encountered a problem and needs to close.",
        "Restart the app. If recovery is offered, recover your unsaved work.");

    public static ErrorInfo AppTaskUnobserved { get; } = new("YRK-APP-003",
        "A background task could not be completed.",
        "Try the operation again. If the problem continues, copy the problem report and contact support.");

    public static ErrorInfo ScannerNoDevice { get; } = new("YRK-SCN-001",
        "No scanner found.", "Connect your scanner and try again.");

    public static ErrorInfo ScannerDeviceBusy { get; } = new("YRK-SCN-002",
        "The scanner is busy. Scanned pages have been kept.", "Wait and try again.");

    public static ErrorInfo ScannerPaperJam { get; } = new("YRK-SCN-003",
        "Paper is jammed. Scanned pages have been kept.", "Clear the paper and try Scan more.");

    public static ErrorInfo ScannerFeederEmpty { get; } = new("YRK-SCN-004",
        "The feeder is empty.", "Put paper in the feeder and try again.");

    public static ErrorInfo ScannerFeederEmptyWithPages { get; } = new("YRK-SCN-005",
        "The feeder is empty. Scanned pages have been kept.",
        "Add more paper and try Scan more, or create a file from the scanned pages.");

    public static ErrorInfo ScannerCancelled { get; } = new("YRK-SCN-006",
        "Scanning cancelled. Scanned pages have been kept.",
        "Try Scan more, or create a file from the scanned pages.");

    public static ErrorInfo ScannerUnavailable { get; } = new("YRK-SCN-007",
        "The scanner is not ready.", "Check its power and connection, then find scanners again.");

    public static ErrorInfo ScannerDisconnected { get; } = new("YRK-SCN-008",
        "The scanner connection was lost.",
        "Check the USB cable and turn on the scanner, then press Find scanners again.");

    public static ErrorInfo ScannerOther { get; } = new("YRK-SCN-009",
        "The scanner reported an error.", "Try scanning again.");

    public static ErrorInfo SignatureTrust { get; } = new("YRK-SIG-001", "The certificate could not be trusted.", "Try adding the certificate again.");
    public static ErrorInfo SignatureOpenRevision { get; } = new("YRK-SIG-002", "The signed version could not be opened.", "Try again or save a copy first.");
    public static ErrorInfo SignatureNoRevision { get; } = new("YRK-SIG-003", "No version before signing is stored in this file.", "Use an earlier backup of the document.");
    public static ErrorInfo SignatureInvalidRange { get; } = new("YRK-SIG-004", "The version before signing cannot be saved because the signature data is invalid.", "Use an earlier backup of the document.");
    public static ErrorInfo SignatureChooseName { get; } = new("YRK-SIG-005", "The version before signing cannot replace the open document.", "Choose a different file name.");
    public static ErrorInfo SignatureSaveRevision { get; } = new("YRK-SIG-006", "The version before signing could not be saved.", "Choose another folder and try again.");
    public static ErrorInfo SignatureCertificateExport { get; } = new("YRK-SIG-007", "The certificate could not be exported.", "Choose another folder and try again.");
    public static ErrorInfo SignatureCertificateOpen { get; } = new("YRK-SIG-008", "The certificate could not be opened.", "Choose a valid digital ID and try again.");
    public static ErrorInfo SignatureCreateId { get; } = new("YRK-SIG-009", "The digital ID could not be created.", "Check the information and try again.");
    public static ErrorInfo SignaturePassword { get; } = new("YRK-SIG-010", "The digital ID password is incorrect.", "Enter the correct password and try again.");
    public static ErrorInfo SignatureCopyId { get; } = new("YRK-SIG-011", "The digital ID could not be copied.", "Choose another folder and try again.");
    public static ErrorInfo SignatureChangePassword { get; } = new("YRK-SIG-012", "The digital ID password could not be changed.", "Check the current password and try again.");
    public static ErrorInfo SignaturePasswordRequired { get; } = new("YRK-SIG-013", "Enter the password of this ID first.", "Type the digital ID password and try again.");
    public static ErrorInfo SignatureImportId { get; } = new("YRK-SIG-014", "The digital ID could not be added.", "Choose a valid digital ID file and try again.");
    public static ErrorInfo SignatureIdRequired { get; } = new("YRK-SIG-015", "Choose a digital ID first.", "Select a digital ID from the list.");
    public static ErrorInfo SignatureTimestampUrl { get; } = new("YRK-SIG-016", "The timestamp server address is invalid.", "Enter a valid HTTP or HTTPS address.");
    public static ErrorInfo SignatureNoPrivateKey { get; } = new("YRK-SIG-017", "This ID has no private key, so it cannot sign.", "Choose a digital ID with a private key.");
    public static ErrorInfo SignatureExpired { get; } = new("YRK-SIG-018", "This digital ID has expired.", "Choose a valid digital ID.");
    public static ErrorInfo SignatureNotYetValid { get; } = new("YRK-SIG-019", "This ID is not valid yet.", "Check the computer date or choose another digital ID.");
    public static ErrorInfo SignatureImageImport { get; } = new("YRK-SIG-020", "The file could not be used as a signature.", "Choose another image or PDF file.");
    public static ErrorInfo SignatureTrustedCertificates { get; } = new("YRK-SIG-021", "The trusted certificates could not be updated.", "Check access to the certificate files and try again.");
    public static ErrorInfo SignatureReadIds { get; } = new("YRK-SIG-022", "The digital IDs could not be loaded.", "Check access to the digital ID files and try again.");
    public static ErrorInfo SecurityRead { get; } = new("YRK-SEC-001", "The protected document could not be read.", "Check access to the document and try again.");
    public static ErrorInfo SecurityUnlock { get; } = new("YRK-SEC-002", "The document could not be unlocked.", "Check the password and try again.");
    public static ErrorInfo SecurityRedactionSearch { get; } = new("YRK-SEC-003", "The search for text to redact failed.", "Try searching again.");
    public static ErrorInfo UpdateTimeout { get; } = new("YRK-UPD-001", "The update download timed out.", "Check the internet connection and try again.");
    public static ErrorInfo UpdateVerification { get; } = new("YRK-UPD-002", "An update was found, but it was not signed by YRK. It was deleted and not installed.", "Try checking for updates again later.");
    public static ErrorInfo UpdateCheck { get; } = new("YRK-UPD-003", "Updates could not be checked.", "Check the internet connection and try again later.");
    public static ErrorInfo UpdateApply { get; } = new("YRK-UPD-004", "The update could not be installed.", "Check for updates and download it again.");
    public static ErrorInfo OcrLanguageDownload { get; } = new("YRK-OCR-001", "The language data could not be downloaded.", "Check the internet connection and try again.");
    public static ErrorInfo OcrScanRecognition { get; } = new("YRK-OCR-002", "Text recognition failed. The PDF was saved without searchable text.", "Try recognizing text in the saved PDF again.");
    public static ErrorInfo OcrReviewRender { get; } = new("YRK-OCR-003", "The review page could not be displayed.", "Try opening the review page again.");
    public static ErrorInfo ConvertScanInput { get; } = new("YRK-CNV-001", "Choose at least one file, language and output format.", "Select the missing options and try again.");
    public static ErrorInfo ConvertScanInterrupted { get; } = new("YRK-CNV-002", "Conversion was interrupted unexpectedly.", "Try converting the document again.");
    public static ErrorInfo ConvertScanFailed { get; } = new("YRK-CNV-003", "The scanned document could not be converted.", "Check that the file can be opened and try again.");
    public static ErrorInfo ConvertScanPublish { get; } = new("YRK-CNV-004", "Some output files could not be created.", "Check the results folder and try again.");
    public static ErrorInfo ConvertScanAddFiles { get; } = new("YRK-CNV-005", "The files could not be added.", "Check access to the files and try again.");
    public static ErrorInfo ConvertScanOpenFolder { get; } = new("YRK-CNV-006", "The results folder could not be opened.", "Open the results folder in File Explorer.");
    public static ErrorInfo ConvertScanCopyText { get; } = new("YRK-CNV-007", "The text could not be copied.", "Try copying the text again.");
    public static ErrorInfo ConvertScanProtected { get; } = new("YRK-CNV-008", "Password-protected PDFs cannot be made searchable yet.", "Save an unprotected copy first.");
    public static ErrorInfo ConvertScanAlreadyText { get; } = new("YRK-CNV-009", "Every page already contains text.", "Choose all pages to recognize the text again.");
    public static ErrorInfo ScannerSave { get; } = new("YRK-SCN-010", "The scanned pages could not be saved.", "Choose another folder and try again. If the problem continues, copy the problem report.");

    public static ErrorInfo FileMissing { get; } = new("YRK-FILE-001", "The recent file is no longer available. It was removed from the recent list.", "Locate the file and open it again.");
    public static ErrorInfo FileOpen { get; } = new("YRK-FILE-002", "The document could not be opened.", "Check that the file is readable and try again.");
    public static ErrorInfo FileOpenIllustrator { get; } = new("YRK-FILE-003", "The Illustrator document could not be opened.", "Save it in Illustrator with Create PDF Compatible File enabled, then try again.");
    public static ErrorInfo FileSave { get; } = new("YRK-FILE-004", "The document could not be saved.", "Check folder access and try saving again.");
    public static ErrorInfo FileReplace { get; } = new("YRK-FILE-005", "The original could not be replaced. A separate copy was saved.", "Check the saved copy, then close other programs using the original and save again.");
    public static ErrorInfo FileRecover { get; } = new("YRK-FILE-006", "The unsaved document could not be recovered.", "Try recovery again or open your latest saved copy.");
    public static ErrorInfo FileOpenExternal { get; } = new("YRK-FILE-007", "The link or folder could not be opened.", "Check the default app and access permissions, then try again.");
    public static ErrorInfo PageKeepOne { get; } = new("YRK-PAGE-001", "A PDF must keep at least one page.", "Keep one page and select the other pages to delete.");
    public static ErrorInfo PageCombineName { get; } = new("YRK-PAGE-002", "The combined PDF cannot replace an input file.", "Choose a new name for the combined PDF.");
    public static ErrorInfo PageOperation { get; } = new("YRK-PAGE-003", "The PDF operation could not be completed.", "Check the source documents and try again.");
    public static ErrorInfo PageOutputName { get; } = new("YRK-PAGE-004", "The output cannot replace the open document.", "Choose another name, or use Save to update the open document.");
    public static ErrorInfo PageRange { get; } = new("YRK-PAGE-005", "The page range is invalid.", "Enter page numbers within the document, for example 1-3, 5.");
    public static ErrorInfo ExportPdfAUnsupported { get; } = new("YRK-EXP-001", "This document cannot be saved as PDF/A without changing its appearance. Nothing was saved.", "Use a regular PDF or adjust the document before trying PDF/A again.");
    public static ErrorInfo ExportPdfA { get; } = new("YRK-EXP-002", "The PDF/A document could not be saved.", "Check folder access and try again.");
    public static ErrorInfo ExportFailed { get; } = new("YRK-EXP-003", "The document could not be exported.", "Check the export settings and destination folder, then try again.");
    public static ErrorInfo PrintFailed { get; } = new("YRK-PRN-001", "The document could not be printed.", "Check the printer connection and paper, then try again.");
    public static ErrorInfo PrintList { get; } = new("YRK-PRN-002", "The printers could not be listed.", "Check Windows printer settings and reopen the print dialog.");
    public static ErrorInfo PrintUnavailable { get; } = new("YRK-PRN-003", "The printer is not available.", "Turn on the printer or select another printer.");
    public static ErrorInfo BatchFolder { get; } = new("YRK-BAT-001", "The output folder could not be used.", "Choose a writable folder and try again.");
    public static ErrorInfo BatchFailed { get; } = new("YRK-BAT-002", "Some files could not be processed.", "Check the source files and job settings, then try again.");
    public static ErrorInfo BatchPreview { get; } = new("YRK-BAT-003", "None of the first files could be opened for the preview.", "Choose readable PDF files and try again.");
    public static ErrorInfo BatchProtected { get; } = new("YRK-BAT-004", "A file is protected against this change.", "Unlock it with the owner password before processing it.");
    public static ErrorInfo BatchPassword { get; } = new("YRK-BAT-005", "A file needs a password to open.", "Open and unlock the file before processing it.");
    public static ErrorInfo CadOpen { get; } = new("YRK-CAD-001", "The CAD drawing could not be opened.", "Check that the drawing is a supported DWG or DXF file and try again.");
    public static ErrorInfo EditAnnotation { get; } = new("YRK-EDIT-001", "The annotation could not be changed.", "Try the change again or reopen the document.");
    public static ErrorInfo EditPicture { get; } = new("YRK-EDIT-002", "The picture could not be added.", "Choose a readable image file and try again.");
    public static ErrorInfo EditApply { get; } = new("YRK-EDIT-003", "The changes could not be applied.", "Try again. If needed, save a copy and reopen it.");
    public static ErrorInfo EditUndo { get; } = new("YRK-EDIT-004", "This editing step could not be restored.", "Try again or reopen your latest saved copy.");
    public static ErrorInfo EditTransform { get; } = new("YRK-EDIT-005", "The document operation failed.", "Check the document and operation settings, then try again.");
    public static ErrorInfo EditCountExport { get; } = new("YRK-EDIT-006", "The count list could not be saved.", "Choose a writable folder and try again.");
    public static ErrorInfo EditCompare { get; } = new("YRK-EDIT-007", "This page could not be compared.", "Try another page or reopen both documents.");
    public static ErrorInfo SignatureSaveArt { get; } = new("YRK-SIG-023", "The signature could not be saved for later use.", "Check access to the signature folder and try again.");
    public static ErrorInfo SignatureDeleteArt { get; } = new("YRK-SIG-024", "The saved signature could not be deleted.", "Check access to the signature folder and try again.");
    public static ErrorInfo AppRestart { get; } = new("YRK-APP-004", "YRK PDF could not restart.", "Close and reopen the app when you are ready.");

    public static ErrorInfo EditText { get; } = new("YRK-EDIT-008", "The text could not be changed.", "If the font cannot be edited or the text overlaps an image, use a white box and Add text instead.");
    public static ErrorInfo EditFields { get; } = new("YRK-EDIT-009", "The form fields could not be added.", "Check the document and try adding the fields again.");
    public static ErrorInfo EditSavedItem { get; } = new("YRK-EDIT-010", "The saved item could not be changed safely.", "Try another item or save a copy before editing again.");

    public static IReadOnlyList<ErrorInfo> All { get; } = Array.AsReadOnly<ErrorInfo>([
        FileMissing,
        FileOpen,
        FileOpenIllustrator,
        FileSave,
        FileReplace,
        FileRecover,
        FileOpenExternal,
        PageKeepOne,
        PageCombineName,
        PageOperation,
        PageOutputName,
        PageRange,
        ExportPdfAUnsupported,
        ExportPdfA,
        ExportFailed,
        PrintFailed,
        PrintList,
        PrintUnavailable,
        BatchFolder,
        BatchFailed,
        BatchPreview,
        BatchProtected,
        BatchPassword,
        CadOpen,
        EditAnnotation,
        EditPicture,
        EditApply,
        EditUndo,
        EditTransform,
        EditCountExport,
        EditCompare,
        SignatureSaveArt,
        SignatureDeleteArt,
        AppRestart,
        EditText,
        EditFields,
        EditSavedItem,
        AppDispatcherUnhandled, AppDomainUnhandled, AppTaskUnobserved,
        ScannerNoDevice, ScannerDeviceBusy, ScannerPaperJam, ScannerFeederEmpty,
        ScannerFeederEmptyWithPages, ScannerCancelled, ScannerUnavailable,
        ScannerDisconnected, ScannerOther,
        SignatureTrust, SignatureOpenRevision, SignatureNoRevision, SignatureInvalidRange,
        SignatureChooseName, SignatureSaveRevision, SignatureCertificateExport, SignatureCertificateOpen,
        SignatureCreateId, SignaturePassword, SignatureCopyId, SignatureChangePassword,
        SignaturePasswordRequired, SignatureImportId, SignatureIdRequired, SignatureTimestampUrl,
        SignatureNoPrivateKey, SignatureExpired, SignatureNotYetValid, SignatureImageImport, SignatureTrustedCertificates, SignatureReadIds,
        SecurityRead, SecurityUnlock, SecurityRedactionSearch,
        UpdateTimeout, UpdateVerification, UpdateCheck, UpdateApply,
        OcrLanguageDownload, OcrScanRecognition, OcrReviewRender,
        ConvertScanInput, ConvertScanInterrupted, ConvertScanFailed, ConvertScanPublish,
        ConvertScanAddFiles, ConvertScanOpenFolder, ConvertScanCopyText, ConvertScanProtected, ConvertScanAlreadyText, ScannerSave,
    ]);

    public static ErrorInfo ForScanner(ScannerErrorKind kind, bool hasPages = false) => kind switch
    {
        ScannerErrorKind.NoDevice => ScannerNoDevice,
        ScannerErrorKind.DeviceBusy => ScannerDeviceBusy,
        ScannerErrorKind.PaperJam => ScannerPaperJam,
        ScannerErrorKind.FeederEmpty => hasPages ? ScannerFeederEmptyWithPages : ScannerFeederEmpty,
        ScannerErrorKind.Cancelled => ScannerCancelled,
        ScannerErrorKind.Unavailable => ScannerUnavailable,
        ScannerErrorKind.Disconnected => ScannerDisconnected,
        _ => ScannerOther,
    };
}
