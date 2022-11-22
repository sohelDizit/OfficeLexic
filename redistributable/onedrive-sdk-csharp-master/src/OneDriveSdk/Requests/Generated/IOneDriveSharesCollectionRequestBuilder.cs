// ------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the MIT License.  See License in the project root for license information.
// ------------------------------------------------------------------------------

using System.Collections.Generic;

using Microsoft.Graph;

// **NOTE** This file was generated by a tool and any changes will be overwritten.

namespace Microsoft.OneDrive.Sdk
{
    /// <summary>
    /// The interface IOneDriveSharesCollectionRequestBuilder.
    /// </summary>
    public partial interface IOneDriveSharesCollectionRequestBuilder
    {
        /// <summary>
        /// Builds the request.
        /// </summary>
        /// <returns>The built request.</returns>
        IOneDriveSharesCollectionRequest Request();

        /// <summary>
        /// Builds the request.
        /// </summary>
        /// <param name="options">The query and header options for the request.</param>
        /// <returns>The built request.</returns>
        IOneDriveSharesCollectionRequest Request(IEnumerable<Option> options);

        /// <summary>
        /// Gets an <see cref="IShareRequestBuilder"/> for the specified Share.
        /// </summary>
        /// <param name="id">The ID for the Share.</param>
        /// <returns>The <see cref="IShareRequestBuilder"/>.</returns>
        IShareRequestBuilder this[string id] { get; }
    }
}
