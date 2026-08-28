using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using SecureUpload.Core.Files;
using SecureUpload.Management.Files;
using SecureUpload.Management.Security;

namespace SecureUpload.Management.Pages.Files;

public sealed class DetailsModel(
    FileInventoryService inventoryService,
    CleanFileDownloadService downloads,
    FileDeletionService deletions,
    IOptions<ManagementAuthorizationOptions> authorizationOptions) : PageModel
{
    private const int MaximumAutoRefreshes = 5;
    private const int AutoRefreshDelaySeconds = 5;

    [BindProperty]
    public string DeleteConfirmation { get; set; } = string.Empty;

    public FileLookupResult Lookup { get; private set; } =
        new(FileLookupState.InvalidId, string.Empty);
    public string ReturnUrl { get; private set; } = "/";
    public string PageTitle { get; private set; } = "File details";
    public string? StatusHeading { get; private set; }
    public string? StatusMessage { get; private set; }
    public bool StatusIsAlert { get; private set; }
    public bool ShouldFocusStatus { get; private set; }
    public string? RefreshNowUrl { get; private set; }
    public string? MetaRefreshContent { get; private set; }
    public bool HasStatusPanel => !string.IsNullOrWhiteSpace(StatusHeading);
    public bool CanDownloadClean => Lookup.File?.CanDownloadClean == true;
    public bool CanRequestDeletion => Lookup.File?.CanRequestDeletion == true;
    public string CancelDeleteUrl =>
        Lookup.Found && Lookup.File is { } file
            ? BuildDetailsUrl(file.StableId, ReturnUrl)
            : ReturnUrl;

    public async Task OnGetAsync(
        string? fileId,
        string? returnUrl,
        string? action,
        int? refresh,
        CancellationToken cancellationToken)
    {
        await LoadAsync(
            fileId,
            returnUrl,
            ParseAction(action),
            NormalizeRefreshCount(refresh),
            cancellationToken);
    }

    public async Task<IActionResult> OnGetDownloadAsync(
        string? fileId,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var safeReturnUrl = ManagementAuthorization.SanitizeLocalReturnUrl(returnUrl);
        var download = await downloads.OpenReadAsync(fileId, cancellationToken);

        return download.Disposition switch
        {
            CleanFileDownloadDisposition.Ready when download.Content is not null => CreateDownloadResult(download),
            CleanFileDownloadDisposition.NotAvailable =>
                RedirectToDetails(fileId, safeReturnUrl, DetailsAction.DownloadUnavailable),
            CleanFileDownloadDisposition.IntegrityFailure =>
                RedirectToDetails(fileId, safeReturnUrl, DetailsAction.DownloadIntegrity),
            CleanFileDownloadDisposition.StorageError =>
                RedirectToDetails(fileId, safeReturnUrl, DetailsAction.DownloadError),
            _ => RedirectToDetails(fileId, safeReturnUrl, DetailsAction.None)
        };
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        string? fileId,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        await LoadAsync(fileId, returnUrl, DetailsAction.None, 0, cancellationToken);
        if (!Lookup.Found || Lookup.File is not { } file)
        {
            return Page();
        }

        if (!string.Equals(DeleteConfirmation.Trim(), file.OriginalFileName, StringComparison.Ordinal))
        {
            ModelState.AddModelError(
                nameof(DeleteConfirmation),
                "Type the current file name exactly to confirm permanent deletion.");
            StatusHeading = "Deletion was not requested";
            StatusMessage = "Type the current file name exactly to confirm permanent deletion.";
            StatusIsAlert = true;
            ShouldFocusStatus = true;
            return Page();
        }

        if (!ManagementAuthorization.TryGetValidatedUserObjectId(
                User,
                authorizationOptions.Value,
                out var objectId))
        {
            return Forbid();
        }

        var deletion = await deletions.RequestAsync(fileId, objectId, cancellationToken);
        return deletion.Disposition switch
        {
            FileDeletionDisposition.Requested =>
                RedirectToDetails(fileId, ReturnUrl, DetailsAction.DeleteRequested),
            FileDeletionDisposition.AlreadyDeleting =>
                RedirectToDetails(fileId, ReturnUrl, DetailsAction.DeleteInProgress),
            FileDeletionDisposition.AlreadyDeleted =>
                RedirectToDetails(fileId, ReturnUrl, DetailsAction.DeleteComplete),
            FileDeletionDisposition.StorageError =>
                RedirectToDetails(fileId, ReturnUrl, DetailsAction.DeleteError),
            _ => RedirectToDetails(fileId, ReturnUrl, DetailsAction.None)
        };
    }

    private async Task LoadAsync(
        string? fileId,
        string? returnUrl,
        DetailsAction action,
        int refreshCount,
        CancellationToken cancellationToken)
    {
        ReturnUrl = ManagementAuthorization.SanitizeLocalReturnUrl(returnUrl);
        Lookup = await inventoryService.GetFileAsync(fileId, cancellationToken);
        if (Lookup.File is { } file)
        {
            PageTitle = file.OriginalFileName;
            ConfigureStatusPresentation(file, action, refreshCount);
        }
        else
        {
            ClearStatusPresentation();
        }
    }

    private IActionResult CreateDownloadResult(CleanFileDownloadResult download)
    {
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        var fileResult = File(
            download.Content!,
            "application/octet-stream",
            download.DownloadFileName);
        fileResult.EnableRangeProcessing = false;
        return fileResult;
    }

    private IActionResult RedirectToDetails(
        string? fileId,
        string returnUrl,
        DetailsAction action,
        int? refresh = null)
    {
        var values = BuildRouteValues(fileId, returnUrl, action, refresh);
        return RedirectToPage("/Files/Details", values);
    }

    private string BuildDetailsUrl(
        string fileId,
        string returnUrl,
        DetailsAction action = DetailsAction.None,
        int? refresh = null) =>
        Url.Page("/Files/Details", BuildRouteValues(fileId, returnUrl, action, refresh))
        ?? $"/Files/Details?fileId={Uri.EscapeDataString(fileId)}";

    private RouteValueDictionary BuildRouteValues(
        string? fileId,
        string returnUrl,
        DetailsAction action,
        int? refresh)
    {
        var values = new RouteValueDictionary
        {
            ["fileId"] = fileId,
            ["returnUrl"] = returnUrl
        };

        var actionValue = ToQueryValue(action);
        if (!string.IsNullOrWhiteSpace(actionValue))
        {
            values["action"] = actionValue;
        }

        if (refresh is > 0)
        {
            values["refresh"] = refresh.Value;
        }

        return values;
    }

    private void ConfigureStatusPresentation(
        ManagementFileView file,
        DetailsAction action,
        int refreshCount)
    {
        ClearStatusPresentation();

        if (file.State == FileState.Deleting)
        {
            RefreshNowUrl = BuildDetailsUrl(file.StableId, ReturnUrl, DetailsAction.DeleteInProgress);
            ShouldFocusStatus = action != DetailsAction.None;
            if (refreshCount >= MaximumAutoRefreshes)
            {
                StatusHeading = "Deletion is taking longer than expected";
                StatusMessage =
                    "The processor is still retrying cleanup. No file content is available while deletion is incomplete.";
                StatusIsAlert = true;
                ShouldFocusStatus = true;
                return;
            }

            StatusHeading = action == DetailsAction.DeleteRequested
                ? "Deletion requested"
                : "Deletion in progress";
            StatusMessage =
                "The file is marked Deleting and the processor is finishing cleanup. This page will refresh for a bounded period.";
            MetaRefreshContent =
                $"{AutoRefreshDelaySeconds};url={BuildDetailsUrl(file.StableId, ReturnUrl, DetailsAction.DeleteInProgress, refreshCount + 1)}";
            return;
        }

        if (file.State == FileState.Deleted)
        {
            StatusHeading = "Deletion completed";
            StatusMessage = "The file content has been removed. Only the audit tombstone remains.";
            ShouldFocusStatus = action is DetailsAction.DeleteRequested or
                DetailsAction.DeleteInProgress or
                DetailsAction.DeleteComplete;
            return;
        }

        switch (action)
        {
            case DetailsAction.DownloadUnavailable:
                StatusHeading = "Download is no longer available";
                StatusMessage = "Only files in the current Available state can be downloaded from this page.";
                StatusIsAlert = true;
                ShouldFocusStatus = true;
                break;
            case DetailsAction.DownloadIntegrity:
                StatusHeading = "Clean download is unavailable";
                StatusMessage =
                    "The clean file could not be verified against clean storage, so no content was returned.";
                StatusIsAlert = true;
                ShouldFocusStatus = true;
                break;
            case DetailsAction.DownloadError:
                StatusHeading = "Download could not be started";
                StatusMessage = "Try again. If the problem continues, refresh the page and retry.";
                StatusIsAlert = true;
                ShouldFocusStatus = true;
                break;
            case DetailsAction.DeleteError:
                StatusHeading = "Deletion could not be requested";
                StatusMessage = "Refresh the page and try again. If the state keeps changing, no content will be removed until a request succeeds.";
                StatusIsAlert = true;
                ShouldFocusStatus = true;
                break;
        }
    }

    private void ClearStatusPresentation()
    {
        StatusHeading = null;
        StatusMessage = null;
        StatusIsAlert = false;
        ShouldFocusStatus = false;
        RefreshNowUrl = null;
        MetaRefreshContent = null;
    }

    private static DetailsAction ParseAction(string? action) =>
        action?.Trim().ToLowerInvariant() switch
        {
            "download-unavailable" => DetailsAction.DownloadUnavailable,
            "download-integrity" => DetailsAction.DownloadIntegrity,
            "download-error" => DetailsAction.DownloadError,
            "delete-requested" => DetailsAction.DeleteRequested,
            "delete-in-progress" => DetailsAction.DeleteInProgress,
            "delete-complete" => DetailsAction.DeleteComplete,
            "delete-error" => DetailsAction.DeleteError,
            _ => DetailsAction.None
        };

    private static string? ToQueryValue(DetailsAction action) =>
        action switch
        {
            DetailsAction.DownloadUnavailable => "download-unavailable",
            DetailsAction.DownloadIntegrity => "download-integrity",
            DetailsAction.DownloadError => "download-error",
            DetailsAction.DeleteRequested => "delete-requested",
            DetailsAction.DeleteInProgress => "delete-in-progress",
            DetailsAction.DeleteComplete => "delete-complete",
            DetailsAction.DeleteError => "delete-error",
            _ => null
        };

    private static int NormalizeRefreshCount(int? refresh) =>
        refresh is > 0
            ? Math.Min(refresh.Value, MaximumAutoRefreshes)
            : 0;

    private enum DetailsAction
    {
        None,
        DownloadUnavailable,
        DownloadIntegrity,
        DownloadError,
        DeleteRequested,
        DeleteInProgress,
        DeleteComplete,
        DeleteError
    }
}
