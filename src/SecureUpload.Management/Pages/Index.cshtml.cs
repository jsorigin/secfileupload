using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Extensions;
using SecureUpload.Management.Files;
using SecureUpload.Management.Security;

namespace SecureUpload.Management.Pages;

public sealed class IndexModel(
    IOptions<ManagementAuthorizationOptions> authorizationOptions,
    FileInventoryService inventoryService) : PageModel
{
    public string DisplayName { get; private set; } = string.Empty;
    public string ObjectId { get; private set; } = string.Empty;
    public string RequiredRole { get; private set; } = string.Empty;
    public FileInventoryResult Inventory { get; private set; } =
        new(InventoryLoadState.Empty, new FileInventoryQuery(string.Empty, "all", 1, 25, null), [], 0, 0, 0, 0, 0);
    public string InventorySummary { get; private set; } = string.Empty;
    public string CurrentInventoryPath { get; private set; } = "/";
    public int MaximumPageSize => inventoryService.MaximumPageSize;
    public int MaximumSearchLength => inventoryService.MaximumSearchLength;
    public IReadOnlyList<InventoryFilterOption> FilterOptions => inventoryService.FilterOptions;

    public async Task OnGetAsync(
        [FromQuery(Name = "search")] string? search,
        [FromQuery(Name = "filter")] string? filter,
        [FromQuery(Name = "page")] int? pageNumber,
        [FromQuery(Name = "pageSize")] int? pageSize,
        CancellationToken cancellationToken)
    {
        RequiredRole = authorizationOptions.Value.RequiredRole;
        if (!ManagementAuthorization.TryGetValidatedUserObjectId(User, authorizationOptions.Value, out var objectId))
        {
            throw new InvalidOperationException(
                "The management landing page executed without a validated management principal.");
        }

        ObjectId = objectId;
        DisplayName = User.Identity?.Name ?? string.Empty;
        Inventory = await inventoryService.LoadAsync(search, filter, pageNumber, pageSize, cancellationToken);
        InventorySummary = Inventory.State switch
        {
            InventoryLoadState.Ready =>
                $"Showing {Inventory.FirstItemNumber.ToString(CultureInfo.InvariantCulture)} to {Inventory.LastItemNumber.ToString(CultureInfo.InvariantCulture)} of {Inventory.MatchedCount.ToString(CultureInfo.InvariantCulture)} matching files.",
            InventoryLoadState.Empty => "No files have been uploaded yet.",
            InventoryLoadState.NoMatch => "No files matched the current filters.",
            InventoryLoadState.CapacityExceeded =>
                $"Inventory is unavailable because the safe browsing limit of {inventoryService.Capacity.ToString(CultureInfo.InvariantCulture)} rows was exceeded.",
            InventoryLoadState.StorageError =>
                "The inventory could not be loaded from storage. Try again or look up a known file ID.",
            _ => "Inventory status is unavailable."
        };
        CurrentInventoryPath = BuildInventoryPath(Inventory.Query, inventoryService.DefaultPageSize);
    }

    public IActionResult OnPostSignOut(string? returnUrl) =>
        Redirect(ManagementAuthorization.CreateSignOutPath(
            authorizationOptions.Value,
            returnUrl));

    private static string BuildInventoryPath(FileInventoryQuery query, int defaultPageSize)
    {
        var builder = new QueryBuilder();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            builder.Add("search", query.Search);
        }

        if (!StringComparer.Ordinal.Equals(query.Filter, "all"))
        {
            builder.Add("filter", query.Filter);
        }

        if (query.PageNumber > 1)
        {
            builder.Add("page", query.PageNumber.ToString(CultureInfo.InvariantCulture));
        }

        if (query.PageSize != defaultPageSize)
        {
            builder.Add("pageSize", query.PageSize.ToString(CultureInfo.InvariantCulture));
        }

        return $"/{builder.ToQueryString()}";
    }
}
