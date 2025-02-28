/*----------------------------------------------------------------------------------
// Copyright 2019 Huawei Technologies Co.,Ltd.
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License.  You may obtain a copy of the
// License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations under the License.
//----------------------------------------------------------------------------------*/
using System.Collections.Generic;

namespace OBS.Model
{
    /// <summary>
    /// Parameters in a request for setting bucket tags
    /// </summary>
    public class SetBucketTaggingRequest : ObsBucketWebServiceRequest
    {
        private IList<Tag> tags;

        internal override string GetAction()
        {
            return "SetBucketTagging";
        }

        /// <summary>
        /// Bucket tag set
        /// </summary>
        /// <remarks>
        /// <para>
        /// Mandatory parameter
        /// You can add 10 tags to a bucket at the maximum.
        /// </para>
        /// </remarks>
        public IList<Tag> Tags
        {
            get {
                
                return tags ?? (tags = new List<Tag>());
            }
            set { tags = value; }
        }

    }
}
    


