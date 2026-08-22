using System.Net.Http.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Cloud.Client.Services;

public sealed class OrganizationSession(HttpClient httpClient)
{
    private IReadOnlyList<OrganizationSummary>? organizations;

    public Guid? SelectedOrganizationId { get; private set; }

    public IReadOnlyList<OrganizationSummary> Organizations => organizations ?? [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        organizations ??= await httpClient.GetFromJsonAsync<IReadOnlyList<OrganizationSummary>>(
            "/api/v1/organizations/",
            cancellationToken) ?? [];
        if (SelectedOrganizationId is null && organizations.Count > 0)
        {
            SelectedOrganizationId = organizations[0].Id;
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        organizations = null;
        SelectedOrganizationId = null;
        await LoadAsync(cancellationToken);
    }

    public void Select(Guid organizationId)
    {
        if (!Organizations.Any(organization => organization.Id == organizationId))
        {
            throw new ArgumentOutOfRangeException(nameof(organizationId));
        }

        SelectedOrganizationId = organizationId;
    }
}
