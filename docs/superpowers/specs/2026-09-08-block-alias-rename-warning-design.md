# Design: Warn on Element Type property changes that orphan Block-stored content

**Issue**: [umbraco/Umbraco-CMS#23864](https://github.com/umbraco/Umbraco-CMS/issues/23864)
**Date**: 2026-09-08
**Status**: Draft — pending user review

## Problem

Block List, Block Grid, Block RTE, and Single Block store property values keyed by
property **alias** inside a JSON blob (`BlockPropertyValue.Alias`), not by the stable
property `Key`/id used for ordinary flat-storage properties. When a property on an
Element Type is renamed or removed, `BlockEditorValues.ResolveBlockItemData` silently
drops any stored value whose alias no longer matches a current property, with only a
debug-level log message. There is no warning to the editor, and no undo.

Investigation (this session, prior to this spec) found no existing mechanism ever
warned users about this:

- `ContentTypeChangeTypes.PropertyAliasChanged` exists only to drive published-cache
  invalidation granularity (`ContentTypeCacheRefresher`); no consumer ever surfaces it
  to the client, including `ServerEventSender`, which only inspects `Create`/`Remove`.
- `ElementSwitchValidator` detects "is this element type used in a Block config" but
  is scoped to a different, unrelated operation (flipping the `isElement`/`isDocument`
  flag), introduced by a dedicated PR (#16421) and never generalized.
- The alias input field in the content type designer (`input-with-alias.element.ts`)
  has only ever had a generic lock-icon unlock affordance — no destructive-change
  confirmation, in its full history.

This is a gap, not a regression: no code path was ever built connecting "this alias
change affects Block-stored data" to a user-facing warning.

A separate, more fundamental bug compounds this: `ContentTypeEditingServiceBase
.MapProperty` matches existing property types by current **alias string**, not by the
stable `Key` GUID. This means a rename performed through the modern Management API
path (what the backoffice actually uses) is indistinguishable from "delete old
property, create new property" — which deletes `PropertyDataDto` rows for **any**
property type, not just block-based ones — and makes it unreliable to even detect
"this is a rename" by the time a validator could act on it.

## Goals

- Detect, before save, when a property alias rename or property deletion on an
  Element Type could orphan content stored in a Block List/Grid/RTE/Single Block, and
  warn the user with an option to cancel.
- Fix the `MapProperty` alias-vs-Key bug as a prerequisite, since reliable rename
  detection depends on it.
- Reuse existing detection and UX primitives already established in this codebase
  rather than inventing new ones.

## Non-goals

- **Fix B (backfill/remap):** automatically migrating existing stored Block content
  from the old alias to the new one. Substantially larger effort (JSON-blob rewriting
  across `cmsPropertyData` versions/cultures, republish/cache invalidation); not
  pursued here.
- **Precise impact counts** (e.g., "this affects 12 items"). That requires new
  JSON-blob usage-scanning infrastructure that doesn't exist anywhere in the codebase
  today. This design uses a coarse boolean check instead: "is this Element Type
  referenced by any Block configuration, anywhere?" — the same precision
  `ElementSwitchValidator` already uses for its own (different) check.
- Media Types / Member Types — they cannot be Element Types, so they cannot be
  referenced by a Block configuration, and are not reachable by this bug.

## Design

### 1. Prerequisite fix: `ContentTypeEditingServiceBase.MapProperty`

**File**: `src/Umbraco.Core/Services/ContentTypeEditing/ContentTypeEditingServiceBase.cs`

Current (line ~827):

```csharp
IPropertyType propertyType = existingPropertyTypes.FirstOrDefault(pt => pt.Alias == property.Alias)
                              ?? new PropertyType(_shortStringHelper, dataType) { Key = property.Key };
```

Changed to match by the stable `Key` instead of the mutable `Alias`:

```csharp
IPropertyType propertyType = existingPropertyTypes.FirstOrDefault(pt => pt.Key == property.Key)
                              ?? new PropertyType(_shortStringHelper, dataType) { Key = property.Key };
```

`property.Key` is already mandatory on the incoming request model for every property,
new or existing (per the existing comment on this line acknowledging the gap). New
properties still fall through to the `new PropertyType(...)` branch, since their `Key`
won't be present in `existingPropertyTypes`. Existing properties — including ones
being renamed — now correctly resolve to the same `IPropertyType` instance, with only
its `Alias` updated, rather than being deleted and recreated.

This also fixes the underlying data-loss bug for **all** flat-storage properties
(not just block-based ones), as a side effect: today, renaming any property alias via
the modern editing service is treated as "delete the old property, create a new one,"
which deletes that property's `PropertyDataDto` rows (via
`ContentTypeRepositoryBase.DeletePropertyType`) and reports
`ContentTypeChangeTypes.PropertyRemoved` instead of `PropertyAliasChanged` — matching
what `Change_Property_Alias_Via_EditingService_Emits_PropertyRemoved` currently
documents as the (buggy) status quo.

**Test change**: `tests/Umbraco.Tests.Integration/Umbraco.Core/Services/ContentTypeEditingServiceTests.ChangeTypes.cs`
— `Change_Property_Alias_Via_EditingService_Emits_PropertyRemoved` currently documents
the buggy behavior. After the fix, it should assert `PropertyAliasChanged` instead,
mirroring the already-correct `Change_Property_Alias_InPlace_Emits_PropertyAliasChanged`
(which exercises the legacy `ContentTypeService` path). Per repo policy, confirm this
test fails against the current code before applying the fix, then passes after.

### 2. Detection: extract a reusable block-usage check

**New interface**, `src/Umbraco.Core/Services/ContentTypeEditing/IContentTypeBlockUsageService.cs`:

```csharp
public interface IContentTypeBlockUsageService
{
    /// <summary>
    /// Determines whether the given content type is configured as an allowed
    /// element on any Block-based property editor (Block List, Block Grid,
    /// Block RTE, Single Block, or any future editor with configurable elements).
    /// </summary>
    Task<bool> IsUsedInBlockConfigurationAsync(IContentTypeBase contentType);
}
```

**Implementation** extracts the existing scan currently embedded in
`ElementSwitchValidator.ElementToDocumentNotUsedInBlockStructuresAsync`
(`src/Umbraco.Core/Services/ContentTypeEditing/ElementSwitchValidator.cs:62-76`):
iterate `PropertyEditorCollection` for editors where `SupportsConfigurableElements`,
fetch data types by those editor aliases via `IDataTypeService.GetByEditorAliasAsync`,
and check whether any data type's `ConfiguredElementTypeKeys()` contains the content
type's `Key`.

`ElementSwitchValidator` is updated to depend on `IContentTypeBlockUsageService` and
call it (negated) instead of duplicating the scan — its own public contract
(`ElementToDocumentNotUsedInBlockStructuresAsync`) is unchanged for its existing
caller (`ContentTypeEditingService`'s element-flag-switch validation).

This keeps the scan logic in one place, independently testable, and reusable by both
the existing flag-switch guard and the new rename/delete warning — without changing
behavior for the flag-switch feature.

### 3. API surface

**New controller**, following the exact precedent of the existing
`CompositionReferenceDocumentTypeController` (same domain, same authorization, same
narrow single-purpose shape — consistent with this API's "one operation per
controller" convention, and with the established `/referenced-by` /
`/composition-references` family of query endpoints):

```csharp
[ApiVersion("1.0")]
[Authorize(Policy = AuthorizationPolicies.TreeAccessDocumentTypes)]
public class BlockUsageDocumentTypeController : DocumentTypeControllerBase
{
    [HttpGet("{id:guid}/block-usage")]
    // returns DocumentTypeBlockUsageResponseModel { bool IsUsedInBlockConfiguration }
}
```

Considered and rejected alternatives (see prior discussion in this spec's brainstorming
session):
- **Folding into `DocumentTypeResponseModel`** (the main `GET /document-type/{id}`):
  no precedent anywhere in this API for a computed "usage" flag on an entity's own
  response; every existing "is this referenced" question is served by a dedicated
  endpoint instead.
- **A `/document-type/{id}/validate` pre-flight endpoint**: Document, Media, Member,
  and Element each have `Validate{Create,Update}` endpoints, but DocumentType/MediaType
  don't, and those existing endpoints validate submitted property *values* (mandatory,
  regex, etc.), a different concern from a structural impact-of-change check. Nothing
  to fold into.
- **Extending `CompositionReferenceDocumentTypeController`**: answers an unrelated
  question (composition usage, not Block configuration usage); repurposing it would
  conflate two distinct relationships.

This is a plain `GET`, independent of any specific pending property edit — it answers
"is this content type (by its persisted id) referenced by any Block configuration
right now." It is only meaningful for:
- Content types that **already exist** on the server (have a persisted id) — a
  brand-new, unsaved Element Type cannot yet be referenced by any Block
  configuration, so the frontend skips the call entirely in that case.
- Content types where `isElement === true`.

### 4. Frontend flow

On the content type workspace, at the point where the user triggers a Save, and
separately at the point where the user confirms a property deletion
(`content-type-design-editor-property.element.ts`'s existing `#requestRemove`):

1. If the content type is not an existing, persisted Element Type, skip — proceed as
   today.
2. Diff the workspace's persisted (loaded) property list against the current draft,
   both keyed by property `unique` (the stable `Key`), to find:
   - Renamed: same `unique`, different `alias`.
   - Removed: `unique` present in persisted, absent in draft.
3. If neither is found, proceed as today with no extra call.
4. If either is found, call `GET /document-type/{id}/block-usage`.
5. If `IsUsedInBlockConfiguration` is `false`, proceed as today.
6. If `true`, show `umbConfirmModal` (the existing general-purpose confirm primitive,
   used in 46 places across the codebase including the existing property-delete
   confirm) with a message explaining that this Element Type is used inside a
   Block-based property editor, and that the change may disconnect or lose existing
   stored content under the old alias/property. Only proceed with the Save (or the
   property removal) if the user confirms; abort (leaving the workspace dirty/unsaved)
   if they cancel.

For the property-deletion path specifically: the existing generic delete confirm in
`content-type-design-editor-property.element.ts` (`#requestRemove`) is enhanced to
include this same block-usage check and to fold the block-usage warning into its
existing confirm message when applicable, rather than stacking two separate dialogs.

New user-facing strings are added via the standard localization workflow
(`docs/package-development.md#type-safe-localization-keys` /
`general-add-localization` skill).

### 5. Testing strategy

**Backend:**
- Unit tests for `IContentTypeBlockUsageService`: referenced by a Block Grid/List/RTE
  config → `true`; unreferenced → `false`.
- `ElementSwitchValidator`'s existing tests continue to pass unchanged after it's
  refactored to delegate to the new service (pure internal refactor).
- Flip `Change_Property_Alias_Via_EditingService_Emits_PropertyRemoved` to assert
  `PropertyAliasChanged`; confirm it fails against current code before the
  `MapProperty` fix, passes after (per repo policy on bug-fix tests).
- Integration test for `BlockUsageDocumentTypeController`: 200 with `true`/`false` per
  usage state; 404 for an unknown id.

**Frontend:**
- Unit test for the persisted-vs-draft property diff (rename and removal detection by
  `unique`/`Key`).
- Test that the block-usage endpoint is called only when the diff finds a rename or
  removal, never for unrelated edits (e.g., renaming only the property's display
  Name, or edits on a non-Element document type).

**Manual verification:** reproduce the exact repro steps from issue #23864 in a
running instance before/after the fix — confirm the dialog appears on rename, Cancel
aborts the save, Confirm proceeds and the value is still lost (Fix B is out of scope,
so this remains expected after this change — the improvement is informed consent, not
data preservation).

## Open items for implementation planning

- Exact wording of the confirm dialog copy (rename case vs. delete case) — to be
  finalized during implementation, not blocking this design.
- Whether `IContentTypeBlockUsageService` should accept `IContentTypeBase` or a
  narrower `Guid key`/`Guid[] keys` shape for potential batch use — implementation
  detail, default to matching `ElementSwitchValidator`'s existing parameter shape
  (`IContentTypeBase`) for now.
