// 
// Copyright (c) Microsoft and contributors.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// 
// See the License for the specific language governing permissions and
// limitations under the License.
// 
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Analytics.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

namespace Microsoft.Analytics.Samples.Formats.Json
{
    /// <summary>
    /// JsonExtractor (sample)
    ///
    ///     [
    ///         { c1:r1v1, c2:r1v2, ...}, 
    ///         { c1:r2v2, c2:r2v2, ...}, 
    ///         ...
    ///     ] 
    ///     => IEnumerable[IRow]
    ///     
    /// </summary>
    [SqlUserDefinedExtractor(AtomicFileProcessing=true)]
    public class JsonExtractor : IExtractor
    {
        /// <summary>
        /// Byte array columns assigning modes.
        /// Normal: expecting a serialized byte array. Assigning the deserialized byte array.
        /// BytesString: assigning the UTF-8 encoded byte array of the corresponding JSON string.
        /// BytesStringCompressed: assigning the BytesString-mode array in a GZip-compressed form.
        /// </summary>
        public enum ByteArrayProjectionMode
        {
            Normal,
            BytesString,
            BytesStringCompressed,
        }


        /// <summary/>
        private string rowpath;

        protected ByteArrayProjectionMode byteArrayProjectionMode;

        private int? numOfDocs;

        private Func<Stream, IUpdatableRow, IEnumerable<IRow>> extractFunc;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonExtractor"/> class.
        /// </summary>
        /// <param name="rowpath">Selector expression to select a collection of JSON fragments. Each fragment ought to promote one row in the result set. 
        /// Default: the type of the JSON root object determines: collection - this collection will be the fragment collection,
        /// single object - fragment collection containing only the root object (promotes single row)</param>
        /// <param name="byteArrayProjectionMode">Controls how to assign value to byte array columns.</param>
        /// <param name="numOfDocs">The number of JSON documents to parse. Default: the reader will process till the end of the line.</param>
        /// <param name="skipMalformedObjects">Indicates whether to silently skip malformed JSON objects. Default: false.</param>
        public JsonExtractor(string rowpath = null, ByteArrayProjectionMode byteArrayProjectionMode = ByteArrayProjectionMode.Normal, int? numOfDocs=null, bool skipMalformedObjects = false)
        {
            this.rowpath = rowpath;
            this.byteArrayProjectionMode = byteArrayProjectionMode;
            this.numOfDocs = numOfDocs;
            this.extractFunc = skipMalformedObjects ? (Func<Stream, IUpdatableRow, IEnumerable < IRow >>)ExtractFromStreamSuppressed : ExtractFromStream;

        }

        public override IEnumerable<IRow> Extract(IUnstructuredReader input, IUpdatableRow output)
        {
            foreach (var row in ExtractCore(input.BaseStream, output))
                yield return row;
        }


        protected IEnumerable<IRow> ExtractCore(Stream input, IUpdatableRow output)
        {
            foreach (var row in extractFunc(input, output))
                yield return row;
        }

        IEnumerable<IRow> ExtractFromStreamSuppressed(Stream input, IUpdatableRow output)
        {
            IEnumerator<IRow> enumerator= ExtractFromStream(input, output).GetEnumerator();
            while (true)
            {
                IRow ret = null;
                bool skip = false ;
                try
                {
                    if (!enumerator.MoveNext())
                        break;
                    ret = enumerator.Current;
                }
                catch (JsonReaderException)
                {
                    skip = true;
                }
                // the yield statement is outside the try catch block
                if(!skip)
                    yield return ret;
            }
        }
                
        IEnumerable<IRow> ExtractFromStream(Stream input, IUpdatableRow output)
        {
            int objectsRead = 0;
            using (var reader = GetJsonReader(input))
            {
                // Parse Json one token at a time
                while ((!numOfDocs.HasValue || objectsRead < numOfDocs) && reader.Read())
                {
                    if (reader.TokenType == JsonToken.StartObject)
                    {
                        var token = JToken.Load(reader);

                        // Rows
                        //  All objects are represented as rows
                        foreach (JObject o in SelectChildren(token, this.rowpath))
                        {
                            // All fields are represented as columns
                            this.JObjectToRow(o, output);

                            yield return output.AsReadOnly();
                        }
                        objectsRead++;
                    }
                }
            }
        }

        protected virtual JsonReader GetJsonReader(Stream stream) 
        {
            return (JsonReader) new JsonTextReader(new StreamReader(stream)); 
        }

        /// <summary/>
        private static IEnumerable<JObject>     SelectChildren(JToken root, string path)
        {
            // JObject children (only)
            //   As JObject(fields) have a clear mapping to Row(columns) as opposed to JArray (positional) or JValue(scalar)
            //  Note: 
            //   We ignore other types (as opposed to fail fast) since JSON supports heterogeneous (schema)
            //   We support the values that can be mapped, without failing all of them if one of happens to not be an Object.

            // Path specified
            if(!string.IsNullOrEmpty(path))
            {
                return root.SelectTokens(path).OfType<JObject>();
            }
            
            // Single JObject
            var o = root as JObject;
            if(o != null)
            {
                return new []{o};
            }

            // Multiple JObjects
            return root.Children().OfType<JObject>();
        }
        
        /// <summary/>
        protected virtual void                  JObjectToRow(JObject o, IUpdatableRow row)
        {
            foreach(var c in row.Schema)
            {
                JToken token = null;
                object value = c.DefaultValue;
                
                // All fields are represented as columns
                //  Note: Each JSON row/payload can contain more or less columns than those specified in the row schema
                //  We simply update the row for any column that matches (and in any order).
                if(o.TryGetValue(c.Name, out token) && token != null)
                {
                    // Note: We simply delegate to Json.Net for all data conversions
                    //  For data conversions beyond what Json.Net supports, do an explicit projection:
                    //      ie: SELECT DateTime.Parse(datetime) AS datetime, ...
                    //  Note: Json.Net incorrectly returns null even for some non-nullable types (sbyte)
                    //      We have to correct this by using the default(T) so it can fit into a row value
                    value = JsonFunctions.ConvertToken(token, c.Type, byteArrayProjectionMode) ?? c.DefaultValue;
                }

                // Update
                row.Set<object>(c.Name, value);
            }
        }
    }
}
