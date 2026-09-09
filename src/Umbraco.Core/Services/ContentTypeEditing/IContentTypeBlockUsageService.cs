using Umbraco.Cms.Core.Models;

namespace Umbraco.Cms.Core.Services.ContentTypeEditing;

/// <summary>
///     Determines whether a content type is configured as an allowed element on a
///     Block-based property editor (Block List, Block Grid, Block RTE, Single Block,
///     or any other editor that supports configurable elements).
/// </summary>
public interface IContentTypeBlockUsageService
{
    /// <summary>
    ///     Determines whether the given content type is referenced as an allowed element
    ///     by any Block-based data type configuration.
    /// </summary>
    /// <param name="contentType">The content type to check.</param>
    /// <returns><c>true</c> if the content type is referenced by at least one Block configuration; otherwise, <c>false</c>.</returns>
    Task<bool> IsUsedInBlockConfigurationAsync(IContentTypeBase contentType);
}
