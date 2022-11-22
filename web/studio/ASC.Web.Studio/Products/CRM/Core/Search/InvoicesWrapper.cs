/*
 *
 * (c) Copyright Ascensio System Limited 2010-2021
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/


using System;

using ASC.Core;
using ASC.ElasticSearch;

namespace ASC.Web.CRM.Core.Search
{
    public sealed class InvoicesWrapper : Wrapper
    {
        [ColumnLastModified("last_modifed_on")]
        public override DateTime LastModifiedOn { get; set; }

        [Column("number", 1)]
        public string Number { get; set; }

        [Column("terms", 2)]
        public string Terms { get; set; }

        [Column("description", 3)]
        public string Description { get; set; }

        [Column("purchase_order_number", 4)]
        public string PurchaseOrderNumber { get; set; }

        protected override string Table { get { return "crm_invoice"; } }

        public static implicit operator InvoicesWrapper(ASC.CRM.Core.Entities.Invoice invoice)
        {
            return new InvoicesWrapper
            {
                Id = invoice.ID,
                Number = invoice.Number,
                Terms = invoice.Terms,
                Description = invoice.Description,
                PurchaseOrderNumber = invoice.PurchaseOrderNumber,
                TenantId = CoreContext.TenantManager.GetCurrentTenant().TenantId
            };
        }
    }
}