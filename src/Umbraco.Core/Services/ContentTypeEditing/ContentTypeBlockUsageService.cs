using Umbraco.Cms.Core.Extensions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;

namespace Umbraco.Cms.Core.Services.ContentTypeEditing;

/// <inheritdoc />
public class ContentTypeBlockUsageService : IContentTypeBlockUsageService
{
    private readonly PropertyEditorCollection _propertyEditorCollection;
    private readonly IDataTypeService _dataTypeService;

    public ContentTypeBlockUsageService(PropertyEditorCollection propertyEditorCollection, IDataTypeService dataTypeService)
    {
        _propertyEditorCollection = propertyEditorCollection;
        _dataTypeService = dataTypeService;
    }

    /// <inheritdoc />
    public async Task<bool> IsUsedInBlockConfigurationAsync(IContentTypeBase contentType)
    {
        // get all propertyEditors that support block usage
        IDataEditor[] editors = _propertyEditorCollection.Where(pe => pe.SupportsConfigurableElements).ToArray();
        var blockEditorAliases = editors.Select(pe => pe.Alias).ToArray();

        // get all dataTypes that are based on those propertyEditors
        IEnumerable<IDataType> dataTypes = await _dataTypeService.GetByEditorAliasAsync(blockEditorAliases);

        // if any dataType has a configuration where this element is selected as a possible block, it is used.
        return dataTypes.Any(dataType =>
            editors.First(editor => editor.Alias == dataType.EditorAlias)
                .GetValueEditor(dataType.ConfigurationObject)
                .ConfiguredElementTypeKeys().Contains(contentType.Key));
    }
}
