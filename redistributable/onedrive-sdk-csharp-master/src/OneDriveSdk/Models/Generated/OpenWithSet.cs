// ------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the MIT License.  See License in the project root for license information.
// ------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Runtime.Serialization;

using Microsoft.Graph;

using Newtonsoft.Json;

// **NOTE** This file was generated by a tool and any changes will be overwritten.


namespace Microsoft.OneDrive.Sdk
{
    /// <summary>
    /// The type OpenWithSet.
    /// </summary>
    [DataContract]
    [JsonConverter(typeof(DerivedTypeConverter))]
    public partial class OpenWithSet
    {
    
        /// <summary>
        /// Gets or sets web.
        /// </summary>
        [DataMember(Name = "web", EmitDefaultValue = false, IsRequired = false)]
        public OpenWithApp Web { get; set; }
    
        /// <summary>
        /// Gets or sets webEmbed.
        /// </summary>
        [DataMember(Name = "webEmbed", EmitDefaultValue = false, IsRequired = false)]
        public OpenWithApp WebEmbed { get; set; }
    
        /// <summary>
        /// Gets or sets additional data.
        /// </summary>
        [JsonExtensionData(ReadData = true)]
        public IDictionary<string, object> AdditionalData { get; set; }
    
    }
}
