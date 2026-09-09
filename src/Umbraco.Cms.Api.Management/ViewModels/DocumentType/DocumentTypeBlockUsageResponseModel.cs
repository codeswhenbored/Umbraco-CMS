namespace Umbraco.Cms.Api.Management.ViewModels.DocumentType;

/// <summary>
/// Represents whether a document type is used as an allowed element in a Block-based property editor configuration.
/// </summary>
public class DocumentTypeBlockUsageResponseModel
{
    /// <summary>
    /// Gets or sets a value indicating whether the document type is referenced by at least one Block-based data type configuration.
    /// </summary>
    public bool IsUsedInBlockConfiguration { get; set; }
}
