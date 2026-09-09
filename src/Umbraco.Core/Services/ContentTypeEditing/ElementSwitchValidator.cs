using Umbraco.Cms.Core.Extensions;
using Umbraco.Cms.Core.Models;

namespace Umbraco.Cms.Core.Services.ContentTypeEditing;

/// <summary>
///     Implementation of <see cref="IElementSwitchValidator"/> for validating element type switching operations.
/// </summary>
/// <remarks>
///     This validator checks constraints when switching content types between document and element modes,
///     ensuring data integrity and preventing invalid configurations.
/// </remarks>
public class ElementSwitchValidator : IElementSwitchValidator
{
    private readonly IContentTypeService _contentTypeService;
    private readonly IContentTypeBlockUsageService _contentTypeBlockUsageService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ElementSwitchValidator"/> class.
    /// </summary>
    /// <param name="contentTypeService">The content type service for querying content type hierarchies.</param>
    /// <param name="contentTypeBlockUsageService">The service used to determine whether a content type is used in a Block configuration.</param>
    public ElementSwitchValidator(
        IContentTypeService contentTypeService,
        IContentTypeBlockUsageService contentTypeBlockUsageService)
    {
        _contentTypeService = contentTypeService;
        _contentTypeBlockUsageService = contentTypeBlockUsageService;
    }

    /// <inheritdoc />
    public Task<bool> AncestorsAreAlignedAsync(IContentType contentType)
    {
        // this call does not return the system roots
        var ancestorIds = contentType.AncestorIds();
        if (ancestorIds.Length == 0)
        {
            // if there are no ancestors, validation passes
            return Task.FromResult(true);
        }

        // if there are any ancestors where IsElement is different from the contentType, the validation fails
        return Task.FromResult(_contentTypeService.GetMany(ancestorIds)
            .Any(ancestor => ancestor.IsElement != contentType.IsElement) is false);
    }

    /// <inheritdoc />
    public Task<bool> DescendantsAreAlignedAsync(IContentType contentType)
    {
        IEnumerable<IContentType> descendants = _contentTypeService.GetDescendants(contentType.Id, false);

        // if there are any descendants where IsElement is different from the contentType, the validation fails
        return Task.FromResult(descendants.Any(descendant => descendant.IsElement != contentType.IsElement) is false);
    }

    /// <inheritdoc />
    public async Task<bool> ElementToDocumentNotUsedInBlockStructuresAsync(IContentTypeBase contentType) =>
        await _contentTypeBlockUsageService.IsUsedInBlockConfigurationAsync(contentType) is false;

    /// <inheritdoc />
    public Task<bool> DocumentToElementHasNoContentAsync(IContentTypeBase contentType) =>
        HasNoContentNodesAsync(contentType);

    /// <inheritdoc />
    public Task<bool> ElementToDocumentHasNoContentAsync(IContentTypeBase contentType) =>
        HasNoContentNodesAsync(contentType);

    private Task<bool> HasNoContentNodesAsync(IContentTypeBase contentType) =>
        Task.FromResult(_contentTypeService.HasContentNodes(contentType.Id) is false);
}
