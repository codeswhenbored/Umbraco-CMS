# Block Alias-Rename Warning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Warn the user, before saving, when renaming or deleting a property on an Element Type would orphan content stored inside a Block List/Grid/RTE/Single Block, and fix the underlying bug that makes alias-rename detection unreliable.

**Architecture:** Fix `ContentTypeEditingServiceBase.MapProperty` to match properties by stable `Key` instead of mutable `Alias`. Extract the existing block-usage scan from `ElementSwitchValidator` into a new, reusable `IContentTypeBlockUsageService`. Expose it via a new narrow `GET /document-type/{id}/block-usage` endpoint, following the exact shape of the existing `CompositionReferenceDocumentTypeController`. On the frontend, diff the workspace's persisted vs. draft property list (by `unique`/Key) at save-time and at property-delete time; when a rename or removal is found on a used-in-blocks Element Type, gate the action behind the existing `umbConfirmModal` primitive.

**Tech Stack:** C# / ASP.NET Core (Umbraco.Core, Umbraco.Cms.Api.Management), NUnit integration tests; TypeScript / Lit web components (Umbraco.Web.UI.Client), Mocha + `@open-wc/testing`.

**Spec:** `docs/superpowers/specs/2026-09-08-block-alias-rename-warning-design.md`

## Global Constraints

- Do not change the public contract of `IElementSwitchValidator` — its existing caller (`ContentTypeEditingService`'s element-flag-switch validation) must see no behavior change.
- The new Management API endpoint follows the "one operation per controller" convention (Management API `CLAUDE.md` §1) — no logic added to existing controllers, no fields added to `DocumentTypeResponseModel`.
- Shared frontend code (`content-type` package, `UmbContentTypeWorkspaceContextBase`, `content-type-design-editor-property.element.ts`) must not import anything from the `document-types` package directly — wire the capability through an optional constructor arg / extension alias, matching the existing `detailRepositoryAlias` indirection pattern (root `CLAUDE.md` §2: "design to the contract").
- Every new/changed backend test must be run and confirmed failing before its corresponding fix, then passing after (root `CLAUDE.md` §10).
- After the new Management API controller is added, `OpenApi.json` and the generated backoffice client must be regenerated and committed together (root `CLAUDE.md`, "Updating OpenApi.json").
- New user-facing strings go through the localization system (`src/Umbraco.Web.UI.Client/src/assets/lang/en.ts` + generated type-safe keys), never inline hardcoded text.

---

## Task 1: Fix `MapProperty` to match by `Key`, not `Alias`

**Files:**
- Modify: `src/Umbraco.Core/Services/ContentTypeEditing/ContentTypeEditingServiceBase.cs:827`
- Modify: `tests/Umbraco.Tests.Integration/Umbraco.Core/Services/ContentTypeEditingServiceTests.ChangeTypes.cs:39-71`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ContentTypeEditingServiceBase<TContentTypeService, TContentType, TContentTypeService2, ...>.MapProperty` now resolves renamed properties to the same `IPropertyType` instance. No signature changes — later tasks don't depend on new types from this task.

- [ ] **Step 1: Update the test to assert the correct (post-fix) behavior**

Replace the test body in `ContentTypeEditingServiceTests.ChangeTypes.cs` (lines 39-71):

```csharp
    [Test]
    public async Task Change_Property_Alias_Via_EditingService_Emits_PropertyAliasChanged()
    {
        var container = ContentTypePropertyContainerModel();
        var propertyType = ContentTypePropertyTypeModel("Title", "title", containerKey: container.Key);
        var contentType = (await ContentTypeEditingService.CreateAsync(
            ContentTypeCreateModel("Test", "test", propertyTypes: [propertyType], containers: [container]),
            Constants.Security.SuperUserKey)).Result!;

        ContentTypeCacheRefresher.JsonPayload[]? refreshedPayloads = null;
        ContentTypeCacheRefreshedNotificationHandler.ContentTypeCacheRefreshed = payloads
            => refreshedPayloads = payloads;

        // Update the property with a different alias but the same key — the editing service
        // must recognize this as a rename, not a remove-and-add
        var updatedPropertyType = ContentTypePropertyTypeModel("Title", "titleRenamed", key: propertyType.Key, containerKey: container.Key);
        var updateModel = ContentTypeUpdateModel("Test", "test", propertyTypes: [updatedPropertyType], containers: [container]);
        var result = await ContentTypeEditingService.UpdateAsync(contentType, updateModel, Constants.Security.SuperUserKey);
        Assert.IsTrue(result.Success);

        Assert.IsNotNull(refreshedPayloads);
        Assert.AreEqual(1, refreshedPayloads!.Length);
        var payload = refreshedPayloads.First();
        Assert.Multiple(() =>
        {
            Assert.IsTrue(payload.ChangeTypes.HasTypesAll(ContentTypeChangeTypes.PropertyAliasChanged), "Expected PropertyAliasChanged flag");
            Assert.IsTrue(payload.ChangeTypes.HasType(ContentTypeChangeTypes.RefreshMain), "PropertyAliasChanged should include RefreshMain");
            Assert.IsFalse(payload.ChangeTypes.HasTypesAll(ContentTypeChangeTypes.PropertyRemoved), "Should NOT have PropertyRemoved");
        });
    }
```

Also delete the now-inapplicable comment above the old test name (the "NOTE: The editing service looks up existing properties by alias..." block) since it documents the bug this task fixes.

- [ ] **Step 2: Run the test to verify it fails against the current (unfixed) code**

Run: `dotnet test tests/Umbraco.Tests.Integration --filter "FullyQualifiedName~Change_Property_Alias_Via_EditingService_Emits_PropertyAliasChanged"`
Expected: FAIL — `payload.ChangeTypes` will have `PropertyRemoved`, not `PropertyAliasChanged` (and the third assertion, `IsFalse(...PropertyRemoved)`, will fail).

- [ ] **Step 3: Apply the fix**

In `ContentTypeEditingServiceBase.cs`, change line 827 from:

```csharp
        IPropertyType propertyType = existingPropertyTypes.FirstOrDefault(pt => pt.Alias == property.Alias)
                                      ?? new PropertyType(_shortStringHelper, dataType)
                                      {
                                          // We are demanding a property type key in the model, so we should probably
                                          // ensure that it's the one that's actually used.
                                          Key = property.Key
                                      };
```

to:

```csharp
        IPropertyType propertyType = existingPropertyTypes.FirstOrDefault(pt => pt.Key == property.Key)
                                      ?? new PropertyType(_shortStringHelper, dataType) { Key = property.Key };
```

- [ ] **Step 4: Run the full `ContentTypeEditingServiceTests.ChangeTypes` fixture to verify the fix and no regressions**

Run: `dotnet test tests/Umbraco.Tests.Integration --filter "FullyQualifiedName~ContentTypeEditingServiceTests"`
Expected: PASS — including `Change_Property_Alias_Via_EditingService_Emits_PropertyAliasChanged`, `Change_Property_Alias_InPlace_Emits_PropertyAliasChanged`, `Remove_Property_And_Add_Property_Emits_Both_Flags`, `Multiple_Structural_Changes_Emit_Combined_Flags` (these last two use distinct property Keys for the removed/added property, so they're unaffected by the fix — confirm they still pass).

- [ ] **Step 5: Commit**

```bash
git add src/Umbraco.Core/Services/ContentTypeEditing/ContentTypeEditingServiceBase.cs tests/Umbraco.Tests.Integration/Umbraco.Core/Services/ContentTypeEditingServiceTests.ChangeTypes.cs
git commit -m "fix(core): match property types by Key instead of Alias when editing content types"
```

---

## Task 2: Extract `IContentTypeBlockUsageService`

**Files:**
- Create: `src/Umbraco.Core/Services/ContentTypeEditing/IContentTypeBlockUsageService.cs`
- Create: `src/Umbraco.Core/Services/ContentTypeEditing/ContentTypeBlockUsageService.cs`
- Modify: `src/Umbraco.Core/Services/ContentTypeEditing/ElementSwitchValidator.cs`
- Modify: `src/Umbraco.Core/DependencyInjection/UmbracoBuilder.cs:482`
- Test: `tests/Umbraco.Tests.Integration/Umbraco.Core/Services/ContentTypeBlockUsageServiceTests.cs`

**Interfaces:**
- Consumes: `PropertyEditorCollection` (ctor-injectable, `Umbraco.Cms.Core.PropertyEditors`), `IDataTypeService.GetByEditorAliasAsync(IEnumerable<string>)` (existing), `IDataEditor.SupportsConfigurableElements` (existing), `IDataValueEditor.ConfiguredElementTypeKeys()` (existing).
- Produces: `IContentTypeBlockUsageService.IsUsedInBlockConfigurationAsync(IContentTypeBase contentType): Task<bool>` — used by Task 4 (the new controller) and by `ElementSwitchValidator` itself.

- [ ] **Step 1: Write the failing test for the new service**

Create `tests/Umbraco.Tests.Integration/Umbraco.Core/Services/ContentTypeBlockUsageServiceTests.cs`:

```csharp
using NUnit.Framework;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.ContentTypeEditing;
using Umbraco.Cms.Tests.Common.Builders;
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
```

- [ ] **Step 2: Run the test to verify it fails to compile / fails**

Run: `dotnet test tests/Umbraco.Tests.Integration --filter "FullyQualifiedName~ContentTypeBlockUsageServiceTests"`
Expected: FAIL to build — `IContentTypeBlockUsageService` does not exist yet.

- [ ] **Step 3: Create the interface**

Create `src/Umbraco.Core/Services/ContentTypeEditing/IContentTypeBlockUsageService.cs`:

```csharp
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
```

- [ ] **Step 4: Create the implementation, extracting the scan from `ElementSwitchValidator`**

Create `src/Umbraco.Core/Services/ContentTypeEditing/ContentTypeBlockUsageService.cs`:

```csharp
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
```

- [ ] **Step 5: Refactor `ElementSwitchValidator` to delegate to the new service (DRY, no public behavior change)**

In `src/Umbraco.Core/Services/ContentTypeEditing/ElementSwitchValidator.cs`, replace the constructor and `ElementToDocumentNotUsedInBlockStructuresAsync`:

```csharp
public class ElementSwitchValidator : IElementSwitchValidator
{
    private readonly IContentTypeService _contentTypeService;
    private readonly IContentTypeBlockUsageService _contentTypeBlockUsageService;

    public ElementSwitchValidator(
        IContentTypeService contentTypeService,
        IContentTypeBlockUsageService contentTypeBlockUsageService)
    {
        _contentTypeService = contentTypeService;
        _contentTypeBlockUsageService = contentTypeBlockUsageService;
    }
```

(remove the now-unused `PropertyEditorCollection` and `IDataTypeService` fields and the `using Umbraco.Cms.Core.PropertyEditors;` import if nothing else in the file needs it)

```csharp
    /// <inheritdoc />
    public async Task<bool> ElementToDocumentNotUsedInBlockStructuresAsync(IContentTypeBase contentType) =>
        await _contentTypeBlockUsageService.IsUsedInBlockConfigurationAsync(contentType) is false;
```

- [ ] **Step 6: Register the new service in DI**

In `src/Umbraco.Core/DependencyInjection/UmbracoBuilder.cs`, next to line 482:

```csharp
            Services.AddUnique<IElementSwitchValidator, ElementSwitchValidator>();
            Services.AddUnique<IContentTypeBlockUsageService, ContentTypeBlockUsageService>();
```

- [ ] **Step 7: Run the new test and confirm it passes**

Run: `dotnet test tests/Umbraco.Tests.Integration --filter "FullyQualifiedName~ContentTypeBlockUsageServiceTests"`
Expected: PASS

- [ ] **Step 8: Run the existing `ElementSwitchValidatorTests` fixture to confirm no behavior change**

Run: `dotnet test tests/Umbraco.Tests.Integration --filter "FullyQualifiedName~ElementSwitchValidatorTests"`
Expected: PASS — all existing cases, including `ElementToDocumentNotUsedInBlockStructures`, unchanged.

- [ ] **Step 9: Commit**

```bash
git add src/Umbraco.Core/Services/ContentTypeEditing/IContentTypeBlockUsageService.cs \
        src/Umbraco.Core/Services/ContentTypeEditing/ContentTypeBlockUsageService.cs \
        src/Umbraco.Core/Services/ContentTypeEditing/ElementSwitchValidator.cs \
        src/Umbraco.Core/DependencyInjection/UmbracoBuilder.cs \
        tests/Umbraco.Tests.Integration/Umbraco.Core/Services/ContentTypeBlockUsageServiceTests.cs
git commit -m "refactor(core): extract IContentTypeBlockUsageService from ElementSwitchValidator"
```

---

## Task 3: New Management API endpoint — `GET /document-type/{id}/block-usage`

**Files:**
- Create: `src/Umbraco.Cms.Api.Management/ViewModels/DocumentType/DocumentTypeBlockUsageResponseModel.cs`
- Create: `src/Umbraco.Cms.Api.Management/Controllers/DocumentType/BlockUsageDocumentTypeController.cs`

**Interfaces:**
- Consumes: `IContentTypeService.GetAsync(Guid): Task<IContentType?>` (existing), `IContentTypeBlockUsageService.IsUsedInBlockConfigurationAsync(IContentTypeBase)` (Task 2), `DocumentTypeControllerBase.OperationStatusResult(ContentTypeOperationStatus)` (existing).
- Produces: `DocumentTypeBlockUsageResponseModel { bool IsUsedInBlockConfiguration }`, route `GET /umbraco/management/api/v1/document-type/{id}/block-usage` — consumed by Task 6 (frontend data source) after the OpenAPI client is regenerated in Task 5.

There is no established precedent for controller-level tests in this API for "referenced-by"-style controllers (`CompositionReferenceDocumentTypeController` has none) — this controller stays untested directly, per the Management API's controllers-are-thin-routing convention (`CLAUDE.md` §1: "avoid business logic directly in controllers"); its logic is already covered by Task 2's tests.

- [ ] **Step 1: Create the response model**

Create `src/Umbraco.Cms.Api.Management/ViewModels/DocumentType/DocumentTypeBlockUsageResponseModel.cs`:

```csharp
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
```

- [ ] **Step 2: Create the controller**

Create `src/Umbraco.Cms.Api.Management/Controllers/DocumentType/BlockUsageDocumentTypeController.cs`:

```csharp
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
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/Umbraco.Cms.Api.Management`
Expected: Build succeeds with no new warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Umbraco.Cms.Api.Management/ViewModels/DocumentType/DocumentTypeBlockUsageResponseModel.cs \
        src/Umbraco.Cms.Api.Management/Controllers/DocumentType/BlockUsageDocumentTypeController.cs
git commit -m "feat(api): add GET document-type/{id}/block-usage endpoint"
```

---

## Task 4: Regenerate `OpenApi.json` and the backoffice client

**Files:**
- Modify: `src/Umbraco.Cms.Api.Management/OpenApi.json`
- Modify: generated files under `src/Umbraco.Web.UI.Client/src/external/backend-api/` (or wherever the hey-api generator writes output — determined by running the generator)

**Interfaces:**
- Consumes: the running `Umbraco.Web.UI` app (built with Task 3's controller) exposing `/umbraco/swagger/management/swagger.json`.
- Produces: a generated `DocumentTypeService.getDocumentTypeByIdBlockUsage({ path: { id } })` client method (exact name confirmed by the generator's output — it will follow the same `get{Entity}By{Param}{Route}` convention as `getDocumentTypeByIdCompositionReferences`) — consumed by Task 6.

- [ ] **Step 1: Run the OpenAPI + client regeneration**

Use the `/umb-update-openapi` skill, or manually:

```bash
# Start Umbraco.Web.UI locally in a non-Production environment if not already running, then:
npm --prefix src/Umbraco.Web.UI.Client run generate:openapi
npm --prefix src/Umbraco.Web.UI.Client run generate:server-api
```

- [ ] **Step 2: Verify the new endpoint and client method exist**

Run: `grep -n "block-usage" src/Umbraco.Cms.Api.Management/OpenApi.json`
Expected: at least one match for the new path.

Run: `grep -rn "getDocumentTypeByIdBlockUsage" src/Umbraco.Web.UI.Client/src/external/backend-api`
Expected: at least one match — note the exact generated method name for use in Task 6.

- [ ] **Step 3: Review the diff for stray formatting-only changes**

Run: `git diff --stat src/Umbraco.Cms.Api.Management/OpenApi.json`
Confirm only substantive additions appear (the new path, the new schema) — per root `CLAUDE.md`, do not hand-reformat this file.

- [ ] **Step 4: Commit**

```bash
git add src/Umbraco.Cms.Api.Management/OpenApi.json src/Umbraco.Web.UI.Client/src/external/backend-api
git commit -m "chore(api): regenerate OpenApi.json and backoffice client for block-usage endpoint"
```

---

## Task 5: Frontend data source + repository for the block-usage check

**Files:**
- Create: `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage/document-type-block-usage.server.data-source.ts`
- Create: `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage/document-type-block-usage.repository.ts`
- Create: `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage/constants.ts`
- Create: `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage/manifests.ts`
- Modify: `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/manifests.ts` (register the new manifest array)

**Interfaces:**
- Consumes: `DocumentTypeService.getDocumentTypeByIdBlockUsage` (Task 4), `tryExecute` (`@umbraco-cms/backoffice/resources`), `UmbRepositoryBase` (`@umbraco-cms/backoffice/repository`).
- Produces: `UmbDocumentTypeBlockUsageRepository.isUsedInBlockConfiguration(unique: string): Promise<UmbDataSourceResponse<boolean>>`, exported alias `UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS = 'Umb.Repository.DocumentType.BlockUsage'` — consumed by Task 7.

- [ ] **Step 1: Create the server data source**

Create `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage/document-type-block-usage.server.data-source.ts`:

```ts
import { DocumentTypeService } from '@umbraco-cms/backoffice/external/backend-api';
import type { UmbControllerHost } from '@umbraco-cms/backoffice/controller-api';
import { tryExecute } from '@umbraco-cms/backoffice/resources';
import type { UmbDataSourceResponse } from '@umbraco-cms/backoffice/repository';

/**
 * A data source for checking whether a Document Type is used in a Block editor configuration.
 * @class UmbDocumentTypeBlockUsageServerDataSource
 */
export class UmbDocumentTypeBlockUsageServerDataSource {
	#host: UmbControllerHost;

	constructor(host: UmbControllerHost) {
		this.#host = host;
	}

	/**
	 * Checks whether the given Document Type is referenced by a Block editor configuration.
	 * @param {string} unique - The unique identifier of the document type.
	 * @returns {Promise<UmbDataSourceResponse<boolean>>} Whether the document type is used in a Block configuration.
	 * @memberof UmbDocumentTypeBlockUsageServerDataSource
	 */
	async isUsedInBlockConfiguration(unique: string): Promise<UmbDataSourceResponse<boolean>> {
		const response = await tryExecute(
			this.#host,
			DocumentTypeService.getDocumentTypeByIdBlockUsage({ path: { id: unique } }),
		);
		const error = response.error;
		const data = response.data?.isUsedInBlockConfiguration;

		return { data, error };
	}
}
```

(If Task 4's generated method name differs from `getDocumentTypeByIdBlockUsage`, use the exact name confirmed there.)

- [ ] **Step 2: Create the repository**

Create `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage/document-type-block-usage.repository.ts`:

```ts
import { UmbDocumentTypeBlockUsageServerDataSource } from './document-type-block-usage.server.data-source.js';
import type { UmbControllerHost } from '@umbraco-cms/backoffice/controller-api';
import { UmbRepositoryBase } from '@umbraco-cms/backoffice/repository';

export class UmbDocumentTypeBlockUsageRepository extends UmbRepositoryBase {
	#blockUsageSource: UmbDocumentTypeBlockUsageServerDataSource;

	constructor(host: UmbControllerHost) {
		super(host);
		this.#blockUsageSource = new UmbDocumentTypeBlockUsageServerDataSource(this);
	}

	async isUsedInBlockConfiguration(unique: string) {
		return this.#blockUsageSource.isUsedInBlockConfiguration(unique);
	}
}

export { UmbDocumentTypeBlockUsageRepository as api };
```

- [ ] **Step 3: Create the constants and manifest**

Create `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage/constants.ts`:

```ts
export const UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS = 'Umb.Repository.DocumentType.BlockUsage';
```

Create `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage/manifests.ts`:

```ts
import { UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS } from './constants.js';

export const manifests: Array<UmbExtensionManifest> = [
	{
		type: 'repository',
		alias: UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS,
		name: 'Document Type Block Usage Repository',
		api: () => import('./document-type-block-usage.repository.js'),
	},
];
```

- [ ] **Step 4: Register the new manifest array**

In `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/manifests.ts`, find where the `composition` manifests array is imported and spread into the exported list, and add the new one alongside it (same pattern, new import):

```ts
import { manifests as blockUsageManifests } from './repository/block-usage/manifests.js';
```

...and include `...blockUsageManifests` in the exported array where the composition manifests are spread.

- [ ] **Step 5: Verify it builds**

Run: `npm --prefix src/Umbraco.Web.UI.Client run build`
Expected: build succeeds with no new TypeScript errors.

- [ ] **Step 6: Commit**

```bash
git add src/Umbraco.Web.UI.Client/src/packages/documents/document-types/repository/block-usage \
        src/Umbraco.Web.UI.Client/src/packages/documents/document-types/manifests.ts
git commit -m "feat(client): add repository for the document-type block-usage check"
```

---

## Task 6: Property rename/removal diff helper

**Files:**
- Create: `src/Umbraco.Web.UI.Client/src/packages/content/content-type/structure/get-renamed-or-removed-properties.function.ts`
- Test: `src/Umbraco.Web.UI.Client/src/packages/content/content-type/structure/get-renamed-or-removed-properties.function.test.ts`

**Interfaces:**
- Consumes: `UmbPropertyTypeModel` (`{ unique: string; alias: string; ... }`, from `../types.js`).
- Produces: `getRenamedOrRemovedProperties(persisted: Array<UmbPropertyTypeModel>, current: Array<UmbPropertyTypeModel>): boolean` — used by Task 7 (save flow) and Task 8 (property delete confirm).

- [ ] **Step 1: Write the failing test**

Create `src/Umbraco.Web.UI.Client/src/packages/content/content-type/structure/get-renamed-or-removed-properties.function.test.ts`:

```ts
import { expect } from '@open-wc/testing';
import { getRenamedOrRemovedProperties } from './get-renamed-or-removed-properties.function.js';
import type { UmbPropertyTypeModel } from '../types.js';

const property = (unique: string, alias: string): UmbPropertyTypeModel =>
	({ unique, alias }) as UmbPropertyTypeModel;

describe('getRenamedOrRemovedProperties', () => {
	it('returns false when nothing changed', () => {
		const persisted = [property('a', 'title')];
		const current = [property('a', 'title')];
		expect(getRenamedOrRemovedProperties(persisted, current)).to.be.false;
	});

	it('returns true when a property alias was renamed', () => {
		const persisted = [property('a', 'title')];
		const current = [property('a', 'titleRenamed')];
		expect(getRenamedOrRemovedProperties(persisted, current)).to.be.true;
	});

	it('returns true when a property was removed', () => {
		const persisted = [property('a', 'title')];
		const current: Array<UmbPropertyTypeModel> = [];
		expect(getRenamedOrRemovedProperties(persisted, current)).to.be.true;
	});

	it('returns false when a new property was added', () => {
		const persisted = [property('a', 'title')];
		const current = [property('a', 'title'), property('b', 'subtitle')];
		expect(getRenamedOrRemovedProperties(persisted, current)).to.be.false;
	});

	it('returns false when persisted is undefined (new, unsaved content type)', () => {
		const current = [property('a', 'title')];
		expect(getRenamedOrRemovedProperties(undefined, current)).to.be.false;
	});
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm --prefix src/Umbraco.Web.UI.Client test -- --files "src/packages/content/content-type/structure/get-renamed-or-removed-properties.function.test.ts"`
Expected: FAIL — module does not exist.

- [ ] **Step 3: Implement the function**

Create `src/Umbraco.Web.UI.Client/src/packages/content/content-type/structure/get-renamed-or-removed-properties.function.ts`:

```ts
import type { UmbPropertyTypeModel } from '../types.js';

/**
 * Determines whether, compared to the persisted state, any property in `current` has had its
 * alias changed, or any persisted property has been removed entirely.
 * @param {Array<UmbPropertyTypeModel> | undefined} persisted - The last-persisted set of properties, or `undefined` for a not-yet-saved content type.
 * @param {Array<UmbPropertyTypeModel>} current - The current draft set of properties.
 * @returns {boolean} `true` if a rename or removal is present.
 */
export function getRenamedOrRemovedProperties(
	persisted: Array<UmbPropertyTypeModel> | undefined,
	current: Array<UmbPropertyTypeModel>,
): boolean {
	if (!persisted) return false;

	return persisted.some((persistedProperty) => {
		const currentProperty = current.find((property) => property.unique === persistedProperty.unique);
		if (!currentProperty) return true; // removed
		return currentProperty.alias !== persistedProperty.alias; // renamed
	});
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npm --prefix src/Umbraco.Web.UI.Client test -- --files "src/packages/content/content-type/structure/get-renamed-or-removed-properties.function.test.ts"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Umbraco.Web.UI.Client/src/packages/content/content-type/structure/get-renamed-or-removed-properties.function.ts \
        src/Umbraco.Web.UI.Client/src/packages/content/content-type/structure/get-renamed-or-removed-properties.function.test.ts
git commit -m "feat(client): add pure helper detecting renamed or removed content type properties"
```

---

## Task 7: Wire the block-usage capability into `UmbContentTypeWorkspaceContextBase` and gate Save

**Files:**
- Modify: `src/Umbraco.Web.UI.Client/src/packages/content/content-type/workspace/content-type-workspace-context-base.ts`
- Modify: `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/workspace/document-type/document-type-workspace.context.ts`
- Modify: `src/Umbraco.Web.UI.Client/src/assets/lang/en.ts` (new localization keys)

**Interfaces:**
- Consumes: `getRenamedOrRemovedProperties` (Task 6), `UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS` (Task 5), `createExtensionApiByAlias` (`@umbraco-cms/backoffice/extension-api`, existing), `umbConfirmModal` (`@umbraco-cms/backoffice/modal`, existing), `getPersistedData()`/`getData()` (existing, `UmbEntityDetailWorkspaceContextBase`).
- Produces: `UmbContentTypeWorkspaceContextBase.isUsedInBlockConfiguration(): Promise<boolean>` — used by Task 8 (property delete confirm) and by this task's own `_update` override.

- [ ] **Step 1: Add an optional `blockUsageRepositoryAlias` arg and the capability method to the base class**

In `src/Umbraco.Web.UI.Client/src/packages/content/content-type/workspace/content-type-workspace-context-base.ts`, extend the args interface and add a field + method. Update the top of the file:

```ts
import { getRenamedOrRemovedProperties } from '../structure/get-renamed-or-removed-properties.function.js';
import { createExtensionApiByAlias } from '@umbraco-cms/backoffice/extension-registry';
import { umbConfirmModal } from '@umbraco-cms/backoffice/modal';

// eslint-disable-next-line @typescript-eslint/no-empty-object-type
export interface UmbContentTypeWorkspaceContextArgs extends UmbEntityDetailWorkspaceContextArgs {
	/**
	 * Alias of a repository implementing `isUsedInBlockConfiguration(unique: string)`, used to warn
	 * before a property rename/removal orphans content stored in a Block-based property editor.
	 * Omit for content type flavors that can never be used as Block elements.
	 */
	blockUsageRepositoryAlias?: string;
}
```

Add a private field and set it in the constructor, then add the public capability method and override `_update`:

```ts
	#blockUsageRepositoryAlias?: string;

	constructor(host: UmbControllerHost, args: UmbContentTypeWorkspaceContextArgs) {
		super(host, args);

		this.#blockUsageRepositoryAlias = args.blockUsageRepositoryAlias;

		this.structure = new UmbContentTypeStructureManager<DetailModelType>(this, args.detailRepositoryAlias);
		// ...existing constructor body continues unchanged...
```

Add near the other public methods (e.g. after the constructor):

```ts
	/**
	 * Checks whether this content type is currently referenced by a Block-based property editor
	 * configuration (Block List, Block Grid, Block RTE, Single Block).
	 * Always resolves to `false` for content type flavors with no `blockUsageRepositoryAlias`
	 * configured, or for a not-yet-saved (unique-less) content type.
	 * @returns {Promise<boolean>} Whether this content type is used in a Block configuration.
	 */
	public async isUsedInBlockConfiguration(): Promise<boolean> {
		if (!this.#blockUsageRepositoryAlias) return false;

		const unique = this.getUnique();
		if (!unique) return false;

		const repository = await createExtensionApiByAlias<{
			isUsedInBlockConfiguration: (unique: string) => Promise<{ data?: boolean }>;
		}>(this, this.#blockUsageRepositoryAlias);

		const { data } = await repository.isUsedInBlockConfiguration(unique);
		return data ?? false;
	}

	/**
	 * Determines whether the current draft has renamed or removed any property compared to the
	 * persisted state, and — if so — whether this content type is used in a Block configuration.
	 * @returns {Promise<boolean>} Whether a Block-usage confirm should be shown before proceeding.
	 */
	async #shouldConfirmBlockUsageImpact(currentData: DetailModelType): Promise<boolean> {
		const isElement = this.structure.getOwnerContentType()?.isElement;
		if (!isElement) return false;

		const persisted = this.getPersistedData();
		if (!getRenamedOrRemovedProperties(persisted?.properties, currentData.properties)) return false;

		return this.isUsedInBlockConfiguration();
	}

	protected override async _update(currentData: DetailModelType) {
		if (await this.#shouldConfirmBlockUsageImpact(currentData)) {
			await umbConfirmModal(this, {
				headline: this.localize.term('contentTypeEditor_blockUsageWarningHeadline'),
				content: this.localize.term('contentTypeEditor_blockUsageWarningMessage'),
				color: 'warning',
				confirmLabel: this.localize.term('general_continue'),
			});
		}

		await super._update(currentData);
	}
```

- [ ] **Step 2: Pass the new alias from the Document Type workspace context**

In `src/Umbraco.Web.UI.Client/src/packages/documents/document-types/workspace/document-type/document-type-workspace.context.ts`, add the import and pass the arg:

```ts
import { UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS } from '../../repository/block-usage/constants.js';
```

```ts
		super(host, {
			workspaceAlias: UMB_DOCUMENT_TYPE_WORKSPACE_ALIAS,
			entityType: UMB_DOCUMENT_TYPE_ENTITY_TYPE,
			detailRepositoryAlias: UMB_DOCUMENT_TYPE_DETAIL_REPOSITORY_ALIAS,
			blockUsageRepositoryAlias: UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS,
		});
```

- [ ] **Step 3: Add the localization keys**

Use the `general-add-localization` skill to add `contentTypeEditor_blockUsageWarningHeadline` and `contentTypeEditor_blockUsageWarningMessage` to `src/Umbraco.Web.UI.Client/src/assets/lang/en.ts` under the existing `contentTypeEditor` section, with copy:

- `contentTypeEditor_blockUsageWarningHeadline`: "Used in a Block configuration"
- `contentTypeEditor_blockUsageWarningMessage`: "This Element Type is used inside a Block List, Block Grid, or Rich Text block configuration. Renaming or removing a property here will disconnect it from any content already stored under its old alias — that content will no longer display or be editable, and may be lost the next time the page is saved. Continue?"

Follow that skill's steps for regenerating the type-safe key exports (this repo has generated files at `src/Umbraco.Web.UI.Client/src/libs/localization-api/known-keys.*.generated.ts` — the skill handles regenerating these).

- [ ] **Step 4: Verify it builds**

Run: `npm --prefix src/Umbraco.Web.UI.Client run build`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Umbraco.Web.UI.Client/src/packages/content/content-type/workspace/content-type-workspace-context-base.ts \
        src/Umbraco.Web.UI.Client/src/packages/documents/document-types/workspace/document-type/document-type-workspace.context.ts \
        src/Umbraco.Web.UI.Client/src/assets/lang/en.ts \
        src/Umbraco.Web.UI.Client/src/libs/localization-api
git commit -m "feat(client): confirm before saving a property rename/removal that affects Block-stored content"
```

---

## Task 8: Extend the property-delete confirm with the same block-usage check

**Files:**
- Modify: `src/Umbraco.Web.UI.Client/src/packages/content/content-type/workspace/views/design/content-type-design-editor-property.element.ts`

**Interfaces:**
- Consumes: `UMB_CONTENT_TYPE_WORKSPACE_CONTEXT` (`@umbraco-cms/backoffice/content-type`, existing), `UmbContentTypeWorkspaceContextBase.isUsedInBlockConfiguration()` (Task 7), `getRenamedOrRemovedProperties` is not needed here directly — deletion is unconditionally a removal.
- Produces: no new public interface; this is a leaf UI change.

- [ ] **Step 1: Consume the workspace context and check block usage before the existing confirm**

In `content-type-design-editor-property.element.ts`, add the import and a field:

```ts
import { UMB_CONTENT_TYPE_WORKSPACE_CONTEXT } from '@umbraco-cms/backoffice/content-type';
```

```ts
	#workspaceContext?: typeof UMB_CONTENT_TYPE_WORKSPACE_CONTEXT.TYPE;

	constructor() {
		super();
		this.consumeContext(UMB_CONTENT_TYPE_WORKSPACE_CONTEXT, (context) => {
			this.#workspaceContext = context;
		});
	}
```

(If the class has no existing constructor, add one; if it does, add the `consumeContext` call inside it alongside any existing setup.)

- [ ] **Step 2: Extend `#requestRemove` to include the block-usage warning when applicable**

Replace `#requestRemove`:

```ts
	async #requestRemove(e: Event) {
		e.preventDefault();
		e.stopImmediatePropagation();
		if (!this._property || !this._property.unique) return;

		const unique = this._property.unique;

		const isUsedInBlockConfiguration = (await this.#workspaceContext?.isUsedInBlockConfiguration()) ?? false;

		// TODO: Do proper localization here: [NL]
		await umbConfirmModal(this, {
			headline: `${this.localize.term('actions_delete')} property`,
			content: isUsedInBlockConfiguration
				? html`<umb-localize key="contentTypeEditor_confirmDeletePropertyUsedInBlockMessage" .args=${[this._property.name ?? unique]}>Are you sure you want to delete the property <strong>${this._property.name ?? unique}</strong>? This Element Type is used inside a Block configuration — any content already stored under this property will be lost.</umb-localize></div>`
				: html`<umb-localize key="contentTypeEditor_confirmDeletePropertyMessage" .args=${[this._property.name ?? unique]}>Are you sure you want to delete the property <strong>${this._property.name ?? unique}</strong></umb-localize></div>`,
			confirmLabel: '#actions_delete',
			color: 'danger',
		});

		this._propertyStructureHelper?.removeProperty(unique);
	}
```

- [ ] **Step 3: Add the new localization key**

Use the `general-add-localization` skill to add `contentTypeEditor_confirmDeletePropertyUsedInBlockMessage` to `src/Umbraco.Web.UI.Client/src/assets/lang/en.ts`, next to `contentTypeEditor_confirmDeletePropertyMessage`, with copy: "Are you sure you want to delete the property <strong>%0%</strong>? This Element Type is used inside a Block configuration — any content already stored under this property will be lost."

- [ ] **Step 4: Verify it builds**

Run: `npm --prefix src/Umbraco.Web.UI.Client run build`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Umbraco.Web.UI.Client/src/packages/content/content-type/workspace/views/design/content-type-design-editor-property.element.ts \
        src/Umbraco.Web.UI.Client/src/assets/lang/en.ts \
        src/Umbraco.Web.UI.Client/src/libs/localization-api
git commit -m "feat(client): warn on property deletion that would orphan Block-stored content"
```

---

## Task 9: Manual verification against the original issue

**Files:** none (verification only).

**Interfaces:** none.

- [ ] **Step 1: Start the app**

Use the `run` skill (or `dotnet run --project src/Umbraco.Web.UI`) to launch a local instance.

- [ ] **Step 2: Reproduce issue #23864's steps and confirm the new behavior**

1. Create an Element Type `heroBlock` with a Textstring property aliased `headline`.
2. Create a Block Grid Data Type allowing `heroBlock`.
3. Add that Data Type to a Document Type and create a page; add a `heroBlock`, populate `headline`, publish.
4. In Settings, rename the property alias from `headline` to `title` and click Save.
5. **Confirm**: the new warning dialog appears before the save completes.
6. Click Cancel: confirm the content type is NOT saved (alias reverts to editable draft state, no request sent).
7. Repeat the rename and click Continue: confirm the save proceeds (Fix B is out of scope, so the underlying data loss on the content document still occurs after confirming — this is expected).
8. Repeat, this time deleting the `title` property instead of renaming it: confirm the delete-confirm dialog now mentions Block usage.
9. As a negative check: rename a property alias on a plain Document Type that is NOT an Element Type (or not used in any Block config) — confirm no extra dialog appears and Save proceeds as before.

- [ ] **Step 3: Report results**

Note pass/fail for each of the above in the PR description or task tracker; do not proceed to marking the plan complete if any step fails — return to the relevant task.

---

## Self-Review Notes

- **Spec coverage**: MapProperty fix → Task 1. Extracted `IContentTypeBlockUsageService` → Task 2. API surface → Tasks 3–4. Frontend diff + confirm on Save → Tasks 6–7. Property-delete bundling (per user's "bundle it in now" decision) → Task 8. Testing strategy from the spec → covered per-task plus Task 9 manual verification.
- **Placeholder scan**: no TBD/TODO left in any step; all code blocks are complete, runnable snippets.
- **Type consistency**: `IContentTypeBlockUsageService.IsUsedInBlockConfigurationAsync(IContentTypeBase)` (Task 2) is the exact signature used by the controller (Task 3). `getRenamedOrRemovedProperties(persisted, current): boolean` (Task 6) is the exact signature called from `#shouldConfirmBlockUsageImpact` (Task 7). `UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS` (Task 5) is the exact string passed as `blockUsageRepositoryAlias` (Task 7) and resolved via `createExtensionApiByAlias` (Task 7).
