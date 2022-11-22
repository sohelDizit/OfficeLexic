// ------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the MIT License.  See License in the project root for license information.
// ------------------------------------------------------------------------------

using Microsoft.Graph;

using Newtonsoft.Json;

// **NOTE** This file was generated by a tool and any changes will be overwritten.


namespace Microsoft.OneDrive.Sdk
{
    /// <summary>
    /// The interface IItemThumbnailsCollectionPage.
    /// </summary>
    [JsonConverter(typeof(InterfaceConverter<ItemThumbnailsCollectionPage>))]
    public interface IItemThumbnailsCollectionPage : ICollectionPage<ThumbnailSet>
    {
        /// <summary>
        /// Gets the next page <see cref="IItemThumbnailsCollectionRequest"/> instance.
        /// </summary>
        IItemThumbnailsCollectionRequest NextPageRequest { get; }

        /// <summary>
        /// Initializes the NextPageRequest property.
        /// </summary>
        void InitializeNextPageRequest(IBaseClient client, string nextPageLinkString);
    }
}
