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
    /// Response to a request for listing multipart uploads
    /// </summary>
    public class ListMultipartUploadsResponse : ObsWebServiceResponse
    {

        private IList<MultipartUpload> multipartUploads;
        private IList<string> commonPrefixes;

        /// <summary>
        /// Bucket name
        /// </summary>
        public string BucketName
        {
            get;
            internal set;
        }

        /// <summary>
        /// Start position for listing multipart uploads (sorted by object name)
        /// </summary>
        public string KeyMarker
        {
            get;
            internal set;
        }

        /// <summary>
        /// Start position for listing multipart uploads (sorted by multipart upload ID)
        /// </summary>
        public string UploadIdMarker
        {
            get;
            internal set;
        }


        /// <summary>
        /// Start position for next listing (sorted by object name)
        /// </summary>
        public string NextKeyMarker
        {
            get;
            internal set;
        }


        /// <summary>
        /// Start position for next listing (sorted by multipart upload ID) 
        /// </summary>
        public string NextUploadIdMarker
        {
            get;
            internal set;
        }

        /// <summary>
        /// Maximum number of listed multipart uploads 
        /// </summary>
        public int? MaxUploads
        {
            get;
            internal set;
        }

        /// <summary>
        /// Check whether the listing results are truncated. 
        /// Value "true" indicates that the results are incomplete while value "false" indicates that the results are complete.
        /// </summary>
        public bool IsTruncated
        {
            get;
            internal set;
        }

        /// <summary>
        /// List of multipart uploads
        /// </summary>
        public IList<MultipartUpload> MultipartUploads
        {
            get 
            {
                

                return multipartUploads ?? (multipartUploads = new List<MultipartUpload>()); 
            }
            internal set { multipartUploads = value; }
        }

        /// <summary>
        /// Object name prefix used in this request
        /// </summary>
        public string Prefix
        {
            get;
            internal set;
        }

        /// <summary>
        /// Group character used in this request
        /// </summary>
        public string Delimiter
        {
            get;
            internal set;
        }

        /// <summary>
        /// List of prefixes to the names of grouped objects
        /// </summary>
        public IList<string> CommonPrefixes
        {
            get
            {
               
                return commonPrefixes ?? (commonPrefixes = new List<string>());
            }
            internal set { commonPrefixes = value; }
        }
    }
}
    


