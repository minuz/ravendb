﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Json.Converters;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Server.Operations
{
    public class ModifyCustomFunctionsOperation : IServerOperation<ModifyCustomFunctionsResult>
    {
        private readonly string _database;
        private readonly string _functions;

        public ModifyCustomFunctionsOperation(string database, string functions)
        {
            _database = database;
            _functions = functions;
        }

        public RavenCommand<ModifyCustomFunctionsResult> GetCommand(DocumentConventions conventions, JsonOperationContext context)
        {
            return new ModifyCustomFunctionsCommand(_database, _functions, context);
        }
    }

    public class ModifyCustomFunctionsCommand : RavenCommand<ModifyCustomFunctionsResult>
    {
        private string _database;
        private string _functions;
        private JsonOperationContext _context;
        public override bool IsReadRequest => false;

        public ModifyCustomFunctionsCommand(string database, string functions, JsonOperationContext context)
        {
            _database = database;
            _functions = functions;
            _context = context;
        }
        public override HttpRequestMessage CreateRequest(ServerNode node, out string url)
        {
            url = $"{node.Url}/admin/modify-custom-function?name={_database}";
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                Content = new BlittableJsonContent(stream =>
                {
                    var json = new DynamicJsonValue
                    {
                        ["Functions"] = _functions,
                    };

                    _context.Write(stream, _context.ReadObject(json, "modify-custom-function"));
                })
            };

            return request;
        }

        public override void SetResponse(BlittableJsonReaderObject response, bool fromCache)
        {
            if (response == null)
                ThrowInvalidResponse();

            Result = JsonDeserializationClient.ModifyCustomFunctionResult(response);
        }
    }
}
