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
