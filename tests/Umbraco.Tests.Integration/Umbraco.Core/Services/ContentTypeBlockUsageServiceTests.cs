using NUnit.Framework;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.ContentTypeEditing;
using Umbraco.Cms.Tests.Common.Builders;
using Umbraco.Cms.Tests.Common.Builders.Extensions;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;

namespace Umbraco.Cms.Tests.Integration.Umbraco.Core.Services;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
internal sealed class ContentTypeBlockUsageServiceTests : UmbracoIntegrationTest
{
    private IContentTypeBlockUsageService ContentTypeBlockUsageService => GetRequiredService<IContentTypeBlockUsageService>();

    private IContentTypeService ContentTypeService => GetRequiredService<IContentTypeService>();

    private IDataTypeService DataTypeService => GetRequiredService<IDataTypeService>();

    [TestCase(false, false, false, true)]
    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    [TestCase(false, false, true, false)]
    public async Task IsUsedInBlockConfigurationAsync(
        bool isUsedInBlockList,
        bool isUsedInBlockGrid,
        bool isUsedInRte,
        bool expectedNotUsed)
    {
        // Arrange
        var elementType = await SetupContentType(true);

        if (isUsedInBlockList)
        {
            await SetupDataType(Constants.PropertyEditors.Aliases.BlockList, elementType.Key);
        }

        if (isUsedInBlockGrid)
        {
            await SetupDataType(Constants.PropertyEditors.Aliases.BlockGrid, elementType.Key);
        }

        if (isUsedInRte)
        {
            await SetupDataType(Constants.PropertyEditors.Aliases.RichText, elementType.Key);
        }

        // Act
        var result = await ContentTypeBlockUsageService.IsUsedInBlockConfigurationAsync(elementType);

        // Assert
        Assert.AreEqual(!expectedNotUsed, result);
    }

    [Test]
    public async Task IsUsedInBlockConfigurationAsync_ReturnsFalse_WhenNotUsedAnywhere()
    {
        var elementType = await SetupContentType(true);

        var result = await ContentTypeBlockUsageService.IsUsedInBlockConfigurationAsync(elementType);

        Assert.IsFalse(result);
    }

    private async Task<IContentType> SetupContentType(bool isElement)
    {
        var typeBuilder = new ContentTypeBuilder()
            .WithIsElement(isElement)
            .WithAllowedInLibrary(isElement);
        var contentType = typeBuilder.Build();
        await ContentTypeService.CreateAsync(contentType, Constants.Security.SuperUserKey);
        return contentType;
    }

    private async Task SetupDataType(string editorAlias, Guid elementKey)
    {
        var dataType = DataTypeBuilder.CreateSimpleElementDataType(IOHelper, editorAlias, elementKey, null);
        await DataTypeService.CreateAsync(dataType, Constants.Security.SuperUserKey);
    }
}
