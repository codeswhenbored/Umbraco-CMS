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
