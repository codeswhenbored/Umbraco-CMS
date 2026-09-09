using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.ViewModels.DocumentType;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.ContentTypeEditing;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Web.Common.Authorization;

namespace Umbraco.Cms.Api.Management.Controllers.DocumentType;

/// <summary>
/// Provides an API endpoint for checking whether a document type is used as an allowed element in a Block-based property editor configuration.
/// </summary>
[ApiVersion("1.0")]
[Authorize(Policy = AuthorizationPolicies.TreeAccessDocumentTypes)]
public class BlockUsageDocumentTypeController : DocumentTypeControllerBase
{
    private readonly IContentTypeService _contentTypeService;
    private readonly IContentTypeBlockUsageService _contentTypeBlockUsageService;

    public BlockUsageDocumentTypeController(
        IContentTypeService contentTypeService,
        IContentTypeBlockUsageService contentTypeBlockUsageService)
    {
        _contentTypeService = contentTypeService;
        _contentTypeBlockUsageService = contentTypeBlockUsageService;
    }

    /// <summary>
    /// Gets whether the specified document type is used as an allowed element in a Block-based property editor configuration.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the document type to check.</param>
    /// <returns>
    /// A <see cref="DocumentTypeBlockUsageResponseModel"/> indicating block usage.
    /// Returns <c>404 Not Found</c> if the document type does not exist.
    /// </returns>
    [HttpGet("{id:guid}/block-usage")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(DocumentTypeBlockUsageResponseModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [EndpointSummary("Gets whether a document type is used in a Block configuration.")]
    [EndpointDescription("Gets whether the specified document type is referenced as an allowed element by any Block-based data type configuration.")]
    public async Task<IActionResult> BlockUsage(CancellationToken cancellationToken, Guid id)
    {
        IContentType? contentType = await _contentTypeService.GetAsync(id);

        if (contentType is null)
        {
            return OperationStatusResult(ContentTypeOperationStatus.NotFound);
        }

        var isUsedInBlockConfiguration = await _contentTypeBlockUsageService.IsUsedInBlockConfigurationAsync(contentType);

        return Ok(new DocumentTypeBlockUsageResponseModel { IsUsedInBlockConfiguration = isUsedInBlockConfiguration });
    }
}
