import { UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS } from './constants.js';

export const manifests: Array<UmbExtensionManifest> = [
	{
		type: 'repository',
		alias: UMB_DOCUMENT_TYPE_BLOCK_USAGE_REPOSITORY_ALIAS,
		name: 'Document Type Block Usage Repository',
		api: () => import('./document-type-block-usage.repository.js'),
	},
];
