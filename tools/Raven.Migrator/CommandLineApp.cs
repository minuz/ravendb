﻿using System;
using Microsoft.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using Raven.Migrator.CosmosDB;
using Raven.Migrator.MongoDB;

namespace Raven.Migrator
{
    internal static class CommandLineApp
    {
        private const string HelpOptionString = "-h | -? | --help";

        private static readonly string MongoDbDescription =
            "Connect to MongoDB, pass configuration as JSON" + Environment.NewLine +
            "   Command - available commands: databases, collections, export" + Environment.NewLine +
            "   ConnectionString - MongoDB connection string" + Environment.NewLine +
            "   DatbaseName - the database name, applicaple only for the collections and export commands" + Environment.NewLine +
            "   MigrateGridFS - (Optional) migrate GridFS that is associated with the provided database" + Environment.NewLine +
            "   CollectionsToMigrate - (Optional) a dictionary of collections to rename during the export";

        private static readonly string CosmosDbDescription =
            "Connect to CosmosDB, pass configuration as JSON" + Environment.NewLine +
            "   Command - available commands: databases, collections, export" + Environment.NewLine +
            "   AzureEndpointUrl - CosmosDB URL" + Environment.NewLine +
            "   PrimaryKey - CosmosDB primary key" + Environment.NewLine +
            "   DatbaseName - the database name, applicaple only for the collections and export commands" + Environment.NewLine +
            "   CollectionsToMigrate - (Optional) a dictionary of collections to rename during the export";

        private static CommandLineApplication _app;

        public static int Run(string[] args)
        {
            if (args == null)
                throw new ArgumentNullException(nameof(args));

            _app = new CommandLineApplication
            {
                Name = "Raven.Migrator",
                Description = "Migration tool from other databases"
            };

            _app.HelpOption(HelpOptionString);

            ConfigureMigrationFromNoSqlDatabaseCommand<MongoDBConfiguration>(
                "mongodb", MongoDbDescription, config => new MongoDBMigrator(config));
            ConfigureMigrationFromNoSqlDatabaseCommand<CosmosDBConfiguration>(
                "cosmosdb", CosmosDbDescription, config => new CosmosDBMigrator(config));

            _app.OnExecute(() =>
            {
                _app.ShowHelp();
                return 1;
            });

            try
            {
                return _app.Execute(args);
            }
            catch (CommandParsingException parsingException)
            {
                return ExitWithError(parsingException.Message, _app);
            }
        }

        private static void ConfigureMigrationFromNoSqlDatabaseCommand<T>(
            string command, string description, Func<T, INoSqlMigrator> getMigrator)
            where T : AbstractMigrationConfiguration
        {
            _app.Command(command, cmd =>
            {
                cmd.ExtendedHelpText = cmd.Description = description;
                cmd.HelpOption(HelpOptionString);

                cmd.OnExecute(() =>
                {
                    try
                    {
                        var configurationString = Console.ReadLine();
                        var configuration = JsonConvert.DeserializeObject<T>(configurationString);

                        if (string.IsNullOrWhiteSpace(configuration.Command))
                            return ExitWithError("Command cannot be null or empty", cmd);

                        var migrator = getMigrator(configuration);
                        switch (configuration.Command)
                        {
                            case "databases":
                                migrator.GetDatabases().Wait();
                                break;
                            case "collections":
                                migrator.GetCollectionsInfo().Wait();
                                break;
                            case "export":
                                migrator.MigrateDatabse().Wait();
                                break;
                            default:
                                return ExitWithError($"Command '{configuration.Command}' doesn't exist" + Environment.NewLine +
                                                     "available commands: databases, collections, export", cmd);
                        }
                    }
                    catch (Exception e)
                    {
                        return ExitWithError($"Failed to run {command} command: {e}", cmd);
                    }

                    return 0;
                });
            });
        }

        private static int ExitWithError(string errMsg, CommandLineApplication cmd)
        {
            cmd.Error.WriteLine(errMsg);
            cmd.ShowHelp();
            return 1;
        }
    }
}