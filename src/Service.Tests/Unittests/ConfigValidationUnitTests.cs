// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable disable
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Service.Configurations;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.Services;
using Azure.DataApiBuilder.Service.Tests.Authorization;
using Azure.DataApiBuilder.Service.Tests.Configuration;
using Azure.DataApiBuilder.Service.Tests.GraphQLBuilder.Helpers;
using Azure.DataApiBuilder.Service.Tests.GraphQLBuilder.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests
{
    /// <summary>
    /// Test class to perform semantic validations on the runtime config object. At this point,
    /// the tests focus on the permissions portion of the entities property within the runtimeconfig object.
    /// </summary>
    [TestClass]
    public class ConfigValidationUnitTests
    {
        /// <summary>
        /// Test method to validate that an appropriate exception is thrown when database policy tries to reference a field
        /// which is not accessible.
        /// </summary>
        /// <param name="dbPolicy">Database policy under test.</param>
        [DataTestMethod]
        [DataRow("@claims.id eq @item.id", DisplayName = "Field id is not accessible")]
        [DataRow("@claims.user_email eq @item.email and @claims.user_name ne @item.name", DisplayName = "Field email is not accessible")]
        public void InaccessibleFieldRequestedByPolicy(string dbPolicy)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: Config.Operation.Create,
                includedCols: new HashSet<string> { "*" },
                excludedCols: new HashSet<string> { "id", "email" },
                databasePolicy: dbPolicy
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // Assert that expected exception is thrown.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() => configValidator.ValidatePermissionsInConfig(runtimeConfig));
            Assert.AreEqual("Not all the columns required by policy are accessible.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        /// <summary>
        /// Test method to validate that only 1 CRUD operation is supported for stored procedure
        /// and every role has that same single operation.
        /// </summary>
        [DataTestMethod]
        [DataRow("anonymous", new object[] { "execute" }, null, null, true, false, DisplayName = "Stored-procedure with valid execute permission only")]
        [DataRow("anonymous", new object[] { "*" }, null, null, true, false, DisplayName = "Stored-procedure with valid wildcard permission only, which resolves to execute")]
        [DataRow("anonymous", new object[] { "execute", "read" }, null, null, false, false, DisplayName = "Invalidly define operation in excess of execute")]
        [DataRow("anonymous", new object[] { "create", "read" }, null, null, false, false, DisplayName = "Stored-procedure with create-read permission")]
        [DataRow("anonymous", new object[] { "update", "read" }, null, null, false, false, DisplayName = "Stored-procedure with update-read permission")]
        [DataRow("anonymous", new object[] { "delete", "read" }, null, null, false, false, DisplayName = "Stored-procedure with delete-read permission")]
        [DataRow("anonymous", new object[] { "create" }, null, null, false, false, DisplayName = "Stored-procedure with invalid create permission")]
        [DataRow("anonymous", new object[] { "read" }, null, null, false, false, DisplayName = "Stored-procedure with invalid read permission")]
        [DataRow("anonymous", new object[] { "update" }, null, null, false, false, DisplayName = "Stored-procedure with invalid update permission")]
        [DataRow("anonymous", new object[] { "delete" }, null, null, false, false, DisplayName = "Stored-procedure with invalid delete permission")]
        [DataRow("anonymous", new object[] { "update", "create" }, null, null, false, false, DisplayName = "Stored-procedure with update-create permission")]
        [DataRow("anonymous", new object[] { "delete", "read", "update" }, null, null, false, false, DisplayName = "Stored-procedure with delete-read-update permission")]
        [DataRow("anonymous", new object[] { "execute" }, "authenticated", new object[] { "execute" }, true, false, DisplayName = "Stored-procedure with valid execute permission on all roles")]
        [DataRow("anonymous", new object[] { "*" }, "authenticated", new object[] { "*" }, true, false, DisplayName = "Stored-procedure with valid wildcard permission on all roles, which resolves to execute")]
        [DataRow("anonymous", new object[] { "execute" }, "authenticated", new object[] { "create" }, false, true, DisplayName = "Stored-procedure with valid execute and invalid create permission")]
        public void InvalidCRUDForStoredProcedure(
            string role1,
            object[] operationsRole1,
            string role2,
            object[] operationsRole2,
            bool isValid,
            bool differentOperationDifferentRoleFailure)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE
            );

            List<PermissionSetting> permissionSettings = new();

            permissionSettings.Add(new(
                role: role1,
                operations: operationsRole1));

            // Adding another role for the entity.
            if (role2 is not null && operationsRole2 is not null)
            {
                permissionSettings.Add(new(
                    role: role2,
                    operations: operationsRole2));
            }

            object entitySource = new DatabaseObjectSource(
                    Type: SourceType.StoredProcedure,
                    Name: "sourceName",
                    Parameters: null,
                    KeyFields: null
                );

            Entity testEntity = new(
                Source: entitySource,
                Rest: true,
                GraphQL: true,
                Permissions: permissionSettings.ToArray(),
                Relationships: null,
                Mappings: null
            );
            runtimeConfig.Entities[AuthorizationHelpers.TEST_ENTITY] = testEntity;
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            try
            {
                configValidator.ValidatePermissionsInConfig(runtimeConfig);
                Assert.AreEqual(true, isValid);
            }
            catch (DataApiBuilderException ex)
            {
                Assert.AreEqual(false, isValid);
                Assert.AreEqual(expected: $"Invalid operation for Entity: {AuthorizationHelpers.TEST_ENTITY}. " +
                            $"Stored procedures can only be configured with the 'execute' operation.", actual: ex.Message);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
            }
        }

        /// <summary>
        /// Test method to validate that an appropriate exception is thrown when there is an invalid action
        /// supplied in the runtimeconfig.
        /// </summary>
        /// <param name="dbPolicy">Database policy.</param>
        /// <param name="action">The action to be validated.</param>
        [DataTestMethod]
        [DataRow("@claims.id eq @item.col1", Config.Operation.Insert, DisplayName = "Invalid action Insert specified in config")]
        [DataRow("@claims.id eq @item.col2", Config.Operation.Upsert, DisplayName = "Invalid action Upsert specified in config")]
        [DataRow("@claims.id eq @item.col3", Config.Operation.UpsertIncremental, DisplayName = "Invalid action UpsertIncremental specified in config")]
        public void InvalidActionSpecifiedForARole(string dbPolicy, Config.Operation action)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: action,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: dbPolicy
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // Assert that expected exception is thrown.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() => configValidator.ValidatePermissionsInConfig(runtimeConfig));
            Assert.AreEqual($"action:{action.ToString()} specified for entity:{AuthorizationHelpers.TEST_ENTITY}," +
                    $" role:{AuthorizationHelpers.TEST_ROLE} is not valid.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        /// <summary>
        /// Test that permission configuration validation fails when a database policy
        /// is defined for the Create operation.
        /// </summary>
        /// <param name="dbPolicy">Database policy.</param>
        /// <param name="action">The action to be validated.</param>
        /// <param name="errorExpected">Whether an error is expected.</param>
        [DataTestMethod]
        [DataRow("1 eq @item.col1", Config.Operation.Create, true, DisplayName = "Database Policy defined for Create fails")]
        [DataRow("", Config.Operation.Create, true, DisplayName = "Database Policy left empty for Create fails")]
        [DataRow(null, Config.Operation.Create, false, DisplayName = "Database Policy NOT defined for Create passes")]
        [DataRow("1 eq @item.col2", Config.Operation.Read, false, DisplayName = "Database Policy defined for Read passes")]
        [DataRow("2 eq @item.col3", Config.Operation.Update, false, DisplayName = "Database Policy defined for Update passes")]
        [DataRow("2 eq @item.col3", Config.Operation.Delete, false, DisplayName = "Database Policy defined for Delete passes")]
        public void AddDatabasePolicyToCreateOperationPermission(string dbPolicy, Config.Operation action, bool errorExpected)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: action,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: dbPolicy
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            try
            {
                configValidator.ValidatePermissionsInConfig(runtimeConfig);
                Assert.IsFalse(errorExpected, message: "Validation expected to have failed.");
            }
            catch (DataApiBuilderException ex)
            {
                Assert.IsTrue(errorExpected, message: "Validation expected to have passed.");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
            }
        }

        /// <summary>
        /// Test method to check that Exception is thrown when Target Entity used in relationship is not defined in the config.
        /// </summary>
        [TestMethod]
        public void TestAddingRelationshipWithInvalidTargetEntity()
        {
            Dictionary<string, Relationship> relationshipMap = new();

            // Creating relationship with an Invalid entity in relationship
            Relationship sampleRelationship = new(
                Cardinality: Cardinality.One,
                TargetEntity: "INVALID_ENTITY",
                SourceFields: null,
                TargetFields: null,
                LinkingObject: null,
                LinkingSourceFields: null,
                LinkingTargetFields: null
            );

            relationshipMap.Add("rname1", sampleRelationship);

            Entity sampleEntity1 = GetSampleEntityUsingSourceAndRelationshipMap(
                source: "TEST_SOURCE1",
                relationshipMap: relationshipMap,
                graphQLdetails: true
            );

            Dictionary<string, Entity> entityMap = new();
            entityMap.Add("SampleEntity1", sampleEntity1);

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.mssql),
                RuntimeSettings: new Dictionary<GlobalSettingsType, object>(),
                Entities: entityMap
                );

            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);
            Mock<ISqlMetadataProvider> _sqlMetadataProvider = new();

            // Assert that expected exception is thrown. Entity used in relationship is Invalid
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationshipsInConfig(runtimeConfig, _sqlMetadataProvider.Object));
            Assert.AreEqual($"entity: {sampleRelationship.TargetEntity} used for relationship is not defined in the config.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        }

        /// <summary>
        /// Test method to check that Exception is thrown when Entity used in the relationship has GraphQL disabled.
        /// </summary>
        [TestMethod]
        public void TestAddingRelationshipWithDisabledGraphQL()
        {
            // creating entity with disabled graphQL
            Entity sampleEntity1 = GetSampleEntityUsingSourceAndRelationshipMap(
                source: "TEST_SOURCE1",
                relationshipMap: null,
                graphQLdetails: false
            );

            Dictionary<string, Relationship> relationshipMap = new();

            Relationship sampleRelationship = new(
                Cardinality: Cardinality.One,
                TargetEntity: "SampleEntity1",
                SourceFields: null,
                TargetFields: null,
                LinkingObject: null,
                LinkingSourceFields: null,
                LinkingTargetFields: null
            );

            relationshipMap.Add("rname1", sampleRelationship);

            // Adding relationshipMap to SampleEntity1 (which has GraphQL enabled)
            Entity sampleEntity2 = GetSampleEntityUsingSourceAndRelationshipMap(
                source: "TEST_SOURCE2",
                relationshipMap: relationshipMap,
                graphQLdetails: true
            );

            Dictionary<string, Entity> entityMap = new();
            entityMap.Add("SampleEntity1", sampleEntity1);
            entityMap.Add("SampleEntity2", sampleEntity2);

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.mssql),
                RuntimeSettings: new Dictionary<GlobalSettingsType, object>(),
                Entities: entityMap
                );

            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);
            Mock<ISqlMetadataProvider> _sqlMetadataProvider = new();

            // Exception should be thrown as we cannot use an entity (with graphQL disabled) in a relationship.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationshipsInConfig(runtimeConfig, _sqlMetadataProvider.Object));
            Assert.AreEqual($"entity: {sampleRelationship.TargetEntity} is disabled for GraphQL.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        }

        /// <summary>
        /// Test method to check that an exception is thrown when LinkingObject was provided
        /// while either LinkingSourceField or SourceField is null, and either targetFields or LinkingTargetField is null.
        /// And the relationship is not defined in the database.
        /// Also verify that after adding foreignKeyPair in the Database, no exception is thrown.
        /// </summary>
        [DataRow(new string[] { "id" }, null, new string[] { "num" }, new string[] { "book_num" }, "SampleEntity1", DisplayName = "LinkingSourceField is null")]
        [DataRow(null, new string[] { "token_id" }, new string[] { "num" }, new string[] { "book_num" }, "SampleEntity1", DisplayName = "SourceField is null")]
        [DataRow(new string[] { "id" }, new string[] { "token_id" }, new string[] { "num" }, null, "SampleEntity2", DisplayName = "LinkingTargetField is null")]
        [DataRow(new string[] { "id" }, new string[] { "token_id" }, null, new string[] { "book_num" }, "SampleEntity2", DisplayName = "TargetField is null")]
        [DataTestMethod]
        public void TestRelationshipWithLinkingObjectNotHavingRequiredFields(
            string[] sourceFields,
            string[] linkingSourceFields,
            string[] targetFields,
            string[] linkingTargetFields,
            string relationshipEntity
        )
        {
            // Creating an EntityMap with two sample entity
            Dictionary<string, Entity> entityMap = GetSampleEntityMap(
                sourceEntity: "SampleEntity1",
                targetEntity: "SampleEntity2",
                sourceFields: sourceFields,
                targetFields: targetFields,
                linkingObject: "TEST_SOURCE_LINK",
                linkingSourceFields: linkingSourceFields,
                linkingTargetFields: linkingTargetFields
            );

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.mssql),
                RuntimeSettings: new Dictionary<GlobalSettingsType, object>(),
                Entities: entityMap
                );

            // Mocking EntityToDatabaseObject
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);
            Mock<ISqlMetadataProvider> _sqlMetadataProvider = new();

            Dictionary<string, DatabaseObject> mockDictionaryForEntityDatabaseObject = new();
            mockDictionaryForEntityDatabaseObject.Add(
                "SampleEntity1",
                new DatabaseTable("dbo", "TEST_SOURCE1")
            );

            mockDictionaryForEntityDatabaseObject.Add(
                "SampleEntity2",
                new DatabaseTable("dbo", "TEST_SOURCE2")
            );

            _sqlMetadataProvider.Setup<Dictionary<string, DatabaseObject>>(x =>
                x.EntityToDatabaseObject).Returns(mockDictionaryForEntityDatabaseObject);

            // To mock the schema name and dbObjectName for linkingObject
            _sqlMetadataProvider.Setup<(string, string)>(x =>
                x.ParseSchemaAndDbTableName("TEST_SOURCE_LINK")).Returns(("dbo", "TEST_SOURCE_LINK"));

            // Exception thrown as foreignKeyPair not found in the DB.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationshipsInConfig(runtimeConfig, _sqlMetadataProvider.Object));
            Assert.AreEqual($"Could not find relationship between Linking Object: TEST_SOURCE_LINK"
                + $" and entity: {relationshipEntity}.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);

            // Mocking ForeignKeyPair to be defined In DB
            _sqlMetadataProvider.Setup<bool>(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE_LINK"), new DatabaseTable("dbo", "TEST_SOURCE1")
                )).Returns(true);

            _sqlMetadataProvider.Setup<bool>(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE_LINK"), new DatabaseTable("dbo", "TEST_SOURCE2")
                )).Returns(true);

            // Since, we have defined the relationship in Database,
            // the engine was able to find foreign key relation and validation will pass.
            configValidator.ValidateRelationshipsInConfig(runtimeConfig, _sqlMetadataProvider.Object);
        }

        /// <summary>
        /// Test method to check that an exception is thrown when relationship exists between the wrong pair. i.e.,
        /// LinkingSourceField OR SourceFields are null and relationship between target and linking object exists, no relationship between source and linking object
        /// LinkingTargetField OR TargetField are null but relationship between source and linking object exists, no relationship between target and linking object
        /// Also Test when the foreignKeyPair exist for correct pair, no exception is thrown. i.e.,
        /// LinkingSourceField OR SourceFields are null but relationship between source and linking object exists, no relationship between target and linking object
        /// LinkingTargetField OR TargetField are null but relationship between target and linking object exists, no relationship between source and linking object
        /// </summary>
        [DataRow(new string[] { "id" }, null, new string[] { "num" }, new string[] { "book_num" }, "SampleEntity1", false, true, false,
            DisplayName = "LinkingSourceField is null, only ForeignKeyPair between LinkingObject and target is present. Invalid Case.")]
        [DataRow(null, new string[] { "token_id" }, new string[] { "num" }, new string[] { "book_num" }, "SampleEntity1", false, true, false,
            DisplayName = "SourceField is null, only ForeignKeyPair between LinkingObject and target is present.  Invalid Case.")]
        [DataRow(new string[] { "id" }, new string[] { "token_id" }, new string[] { "num" }, null, "SampleEntity2", true, false, false,
            DisplayName = "LinkingTargetField is null, only ForeignKeyPair between LinkingObject and source is present. Invalid Case.")]
        [DataRow(new string[] { "id" }, new string[] { "token_id" }, null, new string[] { "book_num" }, "SampleEntity2", true, false, false,
            DisplayName = "TargetField is null, , only ForeignKeyPair between LinkingObject and source is present. Invalid Case.")]
        [DataRow(new string[] { "id" }, null, new string[] { "num" }, new string[] { "book_num" }, "SampleEntity1", true, false, true,
            DisplayName = "LinkingSourceField is null, only ForeignKeyPair between LinkingObject and target is present. Valid Case.")]
        [DataRow(null, new string[] { "token_id" }, new string[] { "num" }, new string[] { "book_num" }, "SampleEntity1", true, false, true,
            DisplayName = "SourceField is null, only ForeignKeyPair between LinkingObject and target is present. Valid Case.")]
        [DataRow(new string[] { "id" }, new string[] { "token_id" }, new string[] { "num" }, null, "SampleEntity2", false, true, true,
            DisplayName = "LinkingTargetField is null, only ForeignKeyPair between LinkingObject and source is present. Valid Case.")]
        [DataRow(new string[] { "id" }, new string[] { "token_id" }, null, new string[] { "book_num" }, "SampleEntity2", false, true, true,
            DisplayName = "TargetField is null, , only ForeignKeyPair between LinkingObject and source is present. Valid Case.")]
        [DataTestMethod]
        public void TestRelationshipForCorrectPairingOfLinkingObjectWithSourceAndTarget(
            string[] sourceFields,
            string[] linkingSourceFields,
            string[] targetFields,
            string[] linkingTargetFields,
            string relationshipEntity,
            bool isForeignKeyPairBetSourceAndLinkingObject,
            bool isForeignKeyPairBetTargetAndLinkingObject,
            bool isValidScenario
        )
        {
            // Creating an EntityMap with two sample entity
            Dictionary<string, Entity> entityMap = GetSampleEntityMap(
                sourceEntity: "SampleEntity1",
                targetEntity: "SampleEntity2",
                sourceFields: sourceFields,
                targetFields: targetFields,
                linkingObject: "TEST_SOURCE_LINK",
                linkingSourceFields: linkingSourceFields,
                linkingTargetFields: linkingTargetFields
            );

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.mssql),
                RuntimeSettings: new Dictionary<GlobalSettingsType, object>(),
                Entities: entityMap
                );

            // Mocking EntityToDatabaseObject
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);
            Mock<ISqlMetadataProvider> _sqlMetadataProvider = new();

            Dictionary<string, DatabaseObject> mockDictionaryForEntityDatabaseObject = new();
            mockDictionaryForEntityDatabaseObject.Add(
                "SampleEntity1",
                new DatabaseTable("dbo", "TEST_SOURCE1")
            );

            mockDictionaryForEntityDatabaseObject.Add(
                "SampleEntity2",
                new DatabaseTable("dbo", "TEST_SOURCE2")
            );

            _sqlMetadataProvider.Setup<Dictionary<string, DatabaseObject>>(x =>
                x.EntityToDatabaseObject).Returns(mockDictionaryForEntityDatabaseObject);

            // To mock the schema name and dbObjectName for linkingObject
            _sqlMetadataProvider.Setup<(string, string)>(x =>
                x.ParseSchemaAndDbTableName("TEST_SOURCE_LINK")).Returns(("dbo", "TEST_SOURCE_LINK"));

            // Mocking ForeignKeyPair to be defined In DB
            _sqlMetadataProvider.Setup<bool>(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE_LINK"), new DatabaseTable("dbo", "TEST_SOURCE1")
                )).Returns(isForeignKeyPairBetSourceAndLinkingObject);

            _sqlMetadataProvider.Setup<bool>(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE_LINK"), new DatabaseTable("dbo", "TEST_SOURCE2")
                )).Returns(isForeignKeyPairBetTargetAndLinkingObject);

            if (isValidScenario)
            {
                // No Exception will be thrown as the relationship exists where it's needed.
                configValidator.ValidateRelationshipsInConfig(runtimeConfig, _sqlMetadataProvider.Object);
            }
            else
            {
                // Exception thrown as foreignKeyPair is not present for the correct pair.
                DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                    configValidator.ValidateRelationshipsInConfig(runtimeConfig, _sqlMetadataProvider.Object));
                Assert.AreEqual($"Could not find relationship between Linking Object: TEST_SOURCE_LINK"
                    + $" and entity: {relationshipEntity}.", ex.Message);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            }
        }

        /// <summary>
        /// Test method to check that an exception is thrown when LinkingObject is null
        /// and either of SourceFields and TargetFields is null in the config.
        /// And the foreignKey pair between source and target is not defined in the database as well.
        /// Also verify that after adding foreignKeyPair in the Database, no exception is thrown.
        /// </summary>
        [DataRow(null, new string[] { "das" }, null, DisplayName = "SourceFields is null")]
        [DataRow(new string[] { "id" }, null, null, DisplayName = "TargetFields is null")]
        [DataRow(null, null, null, DisplayName = "both source and targetFields are null")]
        [DataTestMethod]
        public void TestRelationshipWithNoLinkingObjectAndEitherSourceOrTargetFieldIsNull(
            string[] sourceFields,
            string[] targetFields,
            string linkingObject
        )
        {
            // Creating an EntityMap with two sample entity
            Dictionary<string, Entity> entityMap = GetSampleEntityMap(
                sourceEntity: "SampleEntity1",
                targetEntity: "SampleEntity2",
                sourceFields: sourceFields,
                targetFields: targetFields,
                linkingObject: linkingObject,
                linkingSourceFields: null,
                linkingTargetFields: null
            );

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.mssql),
                RuntimeSettings: new Dictionary<GlobalSettingsType, object>(),
                Entities: entityMap
                );

            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);
            Mock<ISqlMetadataProvider> _sqlMetadataProvider = new();

            Dictionary<string, DatabaseObject> mockDictionaryForEntityDatabaseObject = new();
            mockDictionaryForEntityDatabaseObject.Add(
                "SampleEntity1",
                new DatabaseTable("dbo", "TEST_SOURCE1")
            );

            mockDictionaryForEntityDatabaseObject.Add(
                "SampleEntity2",
                new DatabaseTable("dbo", "TEST_SOURCE2")
            );

            _sqlMetadataProvider.Setup<Dictionary<string, DatabaseObject>>(x =>
                x.EntityToDatabaseObject).Returns(mockDictionaryForEntityDatabaseObject);

            // Exception is thrown as foreignKey pair is not specified in the config, nor defined
            // in the database.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidateRelationshipsInConfig(runtimeConfig, _sqlMetadataProvider.Object));
            Assert.AreEqual($"Could not find relationship between entities:"
                + $" SampleEntity1 and SampleEntity2.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);

            // Mocking ForeignKeyPair to be defined In DB
            _sqlMetadataProvider.Setup<bool>(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE1"), new DatabaseTable("dbo", "TEST_SOURCE2")
                )).Returns(true);

            _sqlMetadataProvider.Setup<bool>(x =>
                x.VerifyForeignKeyExistsInDB(
                    new DatabaseTable("dbo", "TEST_SOURCE2"), new DatabaseTable("dbo", "TEST_SOURCE1")
                )).Returns(true);

            // No Exception is thrown as foreignKey Pair was found in the DB between
            // source and target entity.
            configValidator.ValidateRelationshipsInConfig(runtimeConfig, _sqlMetadataProvider.Object);
        }

        /// <summary>
        /// Test method to validate that an appropriate exception is thrown when there is
        /// one or more empty claimtypes specified in the database policy.
        /// </summary>
        /// <param name="policy"></param>
        [DataTestMethod]
        [DataRow("@claims. eq @item.col1", DisplayName = "Empty claim type test 1")]
        [DataRow("@claims. ne @item.col2", DisplayName = "Empty claim type test 2")]
        public void EmptyClaimTypeSuppliedInPolicy(string dbPolicy)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: Config.Operation.Create,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: dbPolicy
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // Assert that expected exception is thrown.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidatePermissionsInConfig(runtimeConfig));
            Assert.AreEqual("Claimtype cannot be empty.", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        /// <summary>
        /// Test to validate that we are correctly throwing an appropriate exception when the database policy
        /// contains one or more claims with invalid format.
        /// </summary>
        /// <param name="policy">The policy to be parsed.</param>
        [DataTestMethod]
        [DataRow("@claims.user_email eq @item.col1 and @claims.emp/rating eq @item.col2", DisplayName = "/ in claimType")]
        [DataRow("@claims.user$email eq @item.col1 and @claims.emp_rating eq @item.col2", DisplayName = "$ in claimType")]
        [DataRow("@claims.user_email eq @item.col1 and not ( true eq @claims.isemp%loyee or @claims.name eq 'Aaron')"
            , DisplayName = "% in claimType")]
        [DataRow("@claims.user+email eq @item.col1 and @claims.isemployee eq @item.col2", DisplayName = "+ in claimType")]
        [DataRow("@claims.(use(r) eq @item.col1 and @claims.isemployee eq @item.col2", DisplayName = "Parenthesis in claimType 1")]
        [DataRow("@claims.(user_email) eq @item.col1 and @claims.isemployee eq @item.col2", DisplayName = "Parenthesis in claimType 2")]
        public void ParseInvalidDbPolicyWithInvalidClaimTypeFormat(string policy)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: Config.Operation.Create,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: policy
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // Assert that expected exception is thrown.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidatePermissionsInConfig(runtimeConfig));
            Assert.IsTrue(ex.Message.StartsWith("Invalid format for claim type"));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        /// <summary>
        /// Test to validate that only and only for SWA, if claims other than "userId" and
        /// "userDetails" are referenced in the database policy, we fail the validation.
        /// </summary>
        /// <param name="authProvider">Authentication provider like AppService, StaticWebApps.</param>
        /// <param name="dbPolicy">Database policy defined for action.</param>
        /// <param name="action">The action for which database policy is defined.</param>
        /// <param name="errorExpected">Boolean value indicating whether an exception is expected or not.</param>
        [DataTestMethod]
        [DataRow("StaticWebApps", "@claims.userId eq @item.col2", Config.Operation.Read, false, DisplayName = "SWA- Database Policy defined for Read passes")]
        [DataRow("staticwebapps", "@claims.userDetails eq @item.col3", Config.Operation.Update, false, DisplayName = "SWA- Database Policy defined for Update passes")]
        [DataRow("StaticWebAPPs", "@claims.email eq @item.col3", Config.Operation.Delete, true, DisplayName = "SWA- Database Policy defined for Delete fails")]
        [DataRow("appService", "@claims.email eq @item.col3", Config.Operation.Delete, false, DisplayName = "AppService- Database Policy defined for Delete passes")]
        public void TestInvalidClaimsForStaticWebApps(string authProvider, string dbPolicy, Config.Operation action, bool errorExpected)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: action,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: dbPolicy,
                authProvider: authProvider.ToString()
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);
            try
            {
                configValidator.ValidatePermissionsInConfig(runtimeConfig);
                Assert.IsFalse(errorExpected);
            }
            catch (DataApiBuilderException ex)
            {
                Assert.IsTrue(errorExpected);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
                Assert.AreEqual(RuntimeConfigValidator.INVALID_CLAIMS_IN_POLICY_ERR_MSG, ex.Message);
            }
        }

        /// <summary>
        /// Test to validate that wildcard action passes all stages of config validation.
        /// </summary>
        [TestMethod]
        public void WildcardActionSpecifiedForARole()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: Config.Operation.All,
                includedCols: new HashSet<string> { "col1", "col2", "col3" }
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // All the validations would pass, and no exception would be thrown.
            configValidator.ValidatePermissionsInConfig(runtimeConfig);
        }

        /// <summary>
        /// Test to validate that no other field can be present in included set if wildcard is present
        /// in it.
        /// </summary>
        [DataTestMethod]
        [DataRow(Config.Operation.Create, DisplayName = "Wildcard Field with another field in included set and create action")]
        [DataRow(Config.Operation.Update, DisplayName = "Wildcard Field with another field in included set and update action")]
        public void WildCardAndOtherFieldsPresentInIncludeSet(Config.Operation actionOp)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: actionOp,
                includedCols: new HashSet<string> { "*", "col2" }
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // Assert that expected exception is thrown.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidatePermissionsInConfig(runtimeConfig));
            string actionName = actionOp.ToString();
            Assert.AreEqual($"No other field can be present with wildcard in the included set for: entity:{AuthorizationHelpers.TEST_ENTITY}," +
                $" role:{AuthorizationHelpers.TEST_ROLE}, action:{actionName}", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        [DataTestMethod]
        [DataRow(Config.Operation.Create, DisplayName = "Wildcard Field with another field in excluded set and create action")]
        [DataRow(Config.Operation.Update, DisplayName = "Wildcard Field with another field in excluded set and update action")]
        public void WildCardAndOtherFieldsPresentInExcludeSet(Config.Operation actionOp)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: actionOp,
                excludedCols: new HashSet<string> { "*", "col1" }
                );
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // Assert that expected exception is thrown.
            DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidatePermissionsInConfig(runtimeConfig));
            string actionName = actionOp.ToString();
            Assert.AreEqual($"No other field can be present with wildcard in the excluded set for: entity:{AuthorizationHelpers.TEST_ENTITY}," +
                $" role:{AuthorizationHelpers.TEST_ROLE}, action:{actionName}", ex.Message);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
        }

        /// <summary>
        /// Test to validate that differently cased operation names specified in config are deserialised correctly,
        /// and hence they pass config validation stage if they are allowed CRUD operation and fail otherwise.
        /// </summary>
        /// <param name="operationName">Name of the operation configured.</param>
        /// <param name="exceptionExpected">Boolean variable which indicates whether the relevant method call
        /// is expected to return an exception.</param>
        [DataTestMethod]
        [DataRow("CREATE", false, DisplayName = "Valid operation name CREATE specified for action")]
        [DataRow("rEAd", false, DisplayName = "Valid operation name rEAd specified for action")]
        [DataRow("UPDate", false, DisplayName = "Valid operation name UPDate specified for action")]
        [DataRow("DelETe", false, DisplayName = "Valid operation name DelETe specified for action")]
        [DataRow("remove", true, DisplayName = "Invalid operation name remove specified for action")]
        [DataRow("inseRt", true, DisplayName = "Invalid operation name inseRt specified for action")]
        public void TestOperationValidityAndCasing(string operationName, bool exceptionExpected)
        {
            string actionJson = @"{
                                        ""action"": " + $"\"{operationName}\"" + @",
                                        ""policy"": {
                                            ""database"": null
                                          },
                                        ""fields"": {
                                            ""include"": [""*""]
                                          }
                                  }";
            object actionForRole = JsonSerializer.Deserialize<object>(actionJson);

            PermissionSetting permissionForEntity = new(
                role: AuthorizationHelpers.TEST_ROLE,
                operations: new object[] { JsonSerializer.SerializeToElement(actionForRole) });

            Entity sampleEntity = new(
                Source: AuthorizationHelpers.TEST_ENTITY,
                Rest: null,
                GraphQL: null,
                Permissions: new PermissionSetting[] { permissionForEntity },
                Relationships: null,
                Mappings: null
                );

            Dictionary<string, Entity> entityMap = new();
            entityMap.Add(AuthorizationHelpers.TEST_ENTITY, sampleEntity);

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.mssql),
                RuntimeSettings: new Dictionary<GlobalSettingsType, object>(),
                Entities: entityMap
                );

            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);
            if (!exceptionExpected)
            {
                configValidator.ValidatePermissionsInConfig(runtimeConfig);
            }
            else
            {
                DataApiBuilderException ex = Assert.ThrowsException<DataApiBuilderException>(() =>
                configValidator.ValidatePermissionsInConfig(runtimeConfig));

                // Assert that the exception returned is the one we expected.
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
                Assert.AreEqual($"action:{operationName} specified for entity:{AuthorizationHelpers.TEST_ENTITY}," +
                    $" role:{AuthorizationHelpers.TEST_ROLE} is not valid.",
                    ex.Message);
            }
        }

        /// <summary>
        /// Test that entity names from config successfully pass or fail validation.
        /// When a failure is expected: Asserts that an exception is thrown, and with that exception object,
        /// validates the exception's status and substatus codes.
        /// </summary>
        /// <param name="entityNameFromConfig"></param>
        [DataTestMethod]
        [DataRow("entityname", false, DisplayName = "Valid lower case letter as first character")]
        [DataRow("Entityname", false, DisplayName = "Valid upper case letter as first character")]
        [DataRow("Entity_name", false, DisplayName = "Valid _ in body")]
        [DataRow("@entityname", true, DisplayName = "Invalid start character @")]
        [DataRow("_entityname", true, DisplayName = "Invalid start character _")]
        [DataRow("#entityname", true, DisplayName = "Invalid start character #")]
        [DataRow("5Entityname", true, DisplayName = "Invalid start character 5")]
        [DataRow("E.ntityName", true, DisplayName = "Invalid body character .")]
        [DataRow("Entity^Name", true, DisplayName = "Invalid body character ^")]
        [DataRow("Entity&Name", true, DisplayName = "Invalid body character &")]
        [DataRow("Entity name", true, DisplayName = "Invalid body character whitespace")]
        [DataRow("__introspectionField", true, DisplayName = "Invalid double underscore reserved for introspection fields.")]
        public void ValidateGraphQLTypeNamesFromConfig(string entityNameFromConfig, bool expectsException)
        {
            Dictionary<string, Entity> entityCollection = new();

            // Sets only the top level name and enables GraphQL for entity
            Entity entity = SchemaConverterTests.GenerateEmptyEntity();
            entity.GraphQL = true;
            entityCollection.Add(entityNameFromConfig, entity);

            // Sets the top level name to an arbitrary value since it is not used in this check
            // and enables GraphQL for entity by setting the GraphQLSettings.Type to a string.
            entity = SchemaConverterTests.GenerateEmptyEntity();
            entity.GraphQL = new GraphQLEntitySettings(Type: entityNameFromConfig);
            entityCollection.Add("EntityA", entity);

            // Sets the top level name to an arbitrary value since it is not used in this check
            // and enables GraphQL for entity by setting the GraphQLSettings.Type to
            // a SingularPlural object where only Singular is defined.
            entity = SchemaConverterTests.GenerateEmptyEntity();
            SingularPlural singularPlural = new(Singular: entityNameFromConfig, Plural: null);
            entity.GraphQL = new GraphQLEntitySettings(Type: singularPlural);
            entityCollection.Add("EntityB", entity);

            // Sets the top level name to an arbitrary value since it is not used in this check
            // and enables GraphQL for entity by setting the GraphQLSettings.Type to
            // a SingularPlural object where both Singular and Plural are defined.
            entity = SchemaConverterTests.GenerateEmptyEntity();
            singularPlural = new(Singular: entityNameFromConfig, Plural: entityNameFromConfig);
            entity.GraphQL = new GraphQLEntitySettings(Type: singularPlural);
            entityCollection.Add("EntityC", entity);

            if (expectsException)
            {
                DataApiBuilderException dabException = Assert.ThrowsException<DataApiBuilderException>(
                    action: () => RuntimeConfigValidator.ValidateEntityNamesInConfig(entityCollection),
                    message: $"Entity name \"{entityNameFromConfig}\" incorrectly passed validation.");

                Assert.AreEqual(expected: HttpStatusCode.ServiceUnavailable, actual: dabException.StatusCode);
                Assert.AreEqual(expected: DataApiBuilderException.SubStatusCodes.ConfigValidationError, actual: dabException.SubStatusCode);
            }
            else
            {
                RuntimeConfigValidator.ValidateEntityNamesInConfig(entityCollection);
            }
        }

        /// <summary>
        /// Validates that an exception is thrown when
        /// there is a collision in the graphQL queries
        /// generated by the entity definitions.
        /// This test declares entities with the following graphQL
        /// definitions
        /// "book" {
        ///     "graphQL": true
        /// }
        /// "Book" {
        ///     "graphQL": true
        /// }
        /// </summary>
        [TestMethod]
        public void ValidateEntitiesWithGraphQLExposedGenerateDuplicateQueries()
        {
            // Entity Name: Book
            // pk_query: book_by_pk
            // List Query: books
            Entity bookWithUpperCase = GraphQLTestHelpers.GenerateEmptyEntity();
            bookWithUpperCase.GraphQL = new GraphQLEntitySettings(true);

            // Entity Name: book
            // pk_query: book_by_pk
            // List Query: books
            Entity book = GraphQLTestHelpers.GenerateEmptyEntity();
            book.GraphQL = new GraphQLEntitySettings(true);

            SortedDictionary<string, Entity> entityCollection = new();
            entityCollection.Add("book", book);
            entityCollection.Add("Book", bookWithUpperCase);
            ValidateExceptionForDuplicateQueriesDueToEntityDefinitions(entityCollection, "Book");
        }

        /// <summary>
        /// Validates that an exception is thrown when
        /// there is a collision in the graphQL queries
        /// generated by the entity definitions.
        /// This test declares entities with the following graphQL
        /// definitions
        /// "ExecuteBook" {
        ///     "source" :{
        ///         "type": "table"
        ///    }
        ///     "graphQL": true
        /// }
        /// "Book_by_pk" {
        ///     "source" :{
        ///         "type": "stored-procedure"
        ///    }
        ///     "graphQL": true
        /// }
        /// </summary>
        [TestMethod]
        public void ValidateStoredProcedureAndTableGeneratedDuplicateQueries()
        {
            // Entity Name: ExecuteBook
            // Entity Type: table
            // pk_query: executebook_by_pk
            // List Query: executebooks
            Entity bookTable = GraphQLTestHelpers.GenerateEmptyEntity(sourceType: SourceType.Table);
            bookTable.GraphQL = new GraphQLEntitySettings(true);

            // Entity Name: book_by_pk
            // Entity Type: Stored Procedure
            // StoredProcedure Query: executebook_by_pk
            Entity bookByPkStoredProcedure = GraphQLTestHelpers.GenerateEmptyEntity(sourceType: SourceType.StoredProcedure);
            bookByPkStoredProcedure.GraphQL = new GraphQLEntitySettings(true);

            SortedDictionary<string, Entity> entityCollection = new();
            entityCollection.Add("executeBook", bookTable);
            entityCollection.Add("Book_by_pk", bookByPkStoredProcedure);
            ValidateExceptionForDuplicateQueriesDueToEntityDefinitions(entityCollection, "executeBook");
        }

        /// <summary>
        /// Validates that an exception is thrown when
        /// there is a collision in the graphQL mutation
        /// generated by the entity definitions.
        /// This test declares entities with the following graphQL
        /// definitions
        /// "ExecuteBooks" {
        ///     "source" :{
        ///         "type": "table"
        ///    }
        ///     "graphQL": true
        /// }
        /// "AddBook" {
        ///     "source" :{
        ///         "type": "stored-procedure"
        ///    }
        ///     "graphQL": {
        ///         "type": "Books"
        ///     }
        /// }
        /// </summary>
        [TestMethod]
        public void ValidateStoredProcedureAndTableGeneratedDuplicateMutation()
        {
            // Entity Name: Book
            // Entity Type: table
            // mutation generated: createBook, updateBook, deleteBook
            Entity bookTable = GraphQLTestHelpers.GenerateEmptyEntity(sourceType: SourceType.Table);
            bookTable.GraphQL = new GraphQLEntitySettings(true);

            // Entity Name: AddBook
            // Entity Type: Stored Procedure
            // StoredProcedure mutation: createBook
            Entity addBookStoredProcedure = GraphQLTestHelpers.GenerateEntityWithStringType(
                                                type: "Books",
                                                sourceType: SourceType.StoredProcedure);

            SortedDictionary<string, Entity> entityCollection = new();
            entityCollection.Add("ExecuteBooks", bookTable);
            entityCollection.Add("AddBook", addBookStoredProcedure);
            ValidateExceptionForDuplicateQueriesDueToEntityDefinitions(entityCollection, "ExecuteBooks");
        }

        /// <summary>
        /// Validates that an exception is thrown when
        /// there is a collision in the graphQL queries
        /// generated by the entity definitions.
        /// This test declares entities with the following graphQL
        /// definitions
        /// "book" {
        ///     "graphQL": true
        /// }
        /// "book_alt" {
        ///     "graphQL": {
        ///         "type": "book"
        ///     }
        ///  }
        /// </summary>
        [TestMethod]
        public void ValidateEntitiesWithNameCollisionInGraphQLTypeGenerateDuplicateQueriesCase()
        {
            // Entity Name: book
            // pk_query: book_by_pk
            // List Query: books
            Entity book = GraphQLTestHelpers.GenerateEmptyEntity();
            book.GraphQL = new GraphQLEntitySettings(true);

            // Entity Name: book_alt
            // pk_query: book_by_pk
            // List Query: books
            Entity book_alt = GraphQLTestHelpers.GenerateEntityWithStringType("book");

            SortedDictionary<string, Entity> entityCollection = new();
            entityCollection.Add("book", book);
            entityCollection.Add("book_alt", book_alt);
            ValidateExceptionForDuplicateQueriesDueToEntityDefinitions(entityCollection, "book_alt");
        }

        /// <summary>
        /// Validates that an exception is thrown when
        /// there is a collision in the graphQL queries
        /// generated by the entity definitions.
        /// This test declares entities with the following graphQL
        /// definitions
        /// "book" {
        ///     "graphQL": {
        ///         "type": {
        ///             "singular": "book",
        ///             "plural": "books",
        ///         }
        ///     }
        /// }
        /// "book_alt" {
        ///     "graphQL": {
        ///         "type": "book"
        ///     }
        ///  }
        /// </summary>
        [TestMethod]
        public void ValidateEntitiesWithCollisionsInSingularPluralNamesGenerateDuplicateQueries()
        {
            // Entity Name: book
            // pk_query: book_by_pk
            // List Query: books
            Entity book = GraphQLTestHelpers.GenerateEntityWithSingularPlural("book", "books");

            // Entity Name: book_alt
            // pk_query: book_by_pk
            // List Query: books
            Entity book_alt = GraphQLTestHelpers.GenerateEntityWithStringType("book");

            SortedDictionary<string, Entity> entityCollection = new();
            entityCollection.Add("book", book);
            entityCollection.Add("book_alt", book_alt);
            ValidateExceptionForDuplicateQueriesDueToEntityDefinitions(entityCollection, "book_alt");
        }

        /// <summary>
        /// Validates that an exception is thrown when there is a collision
        /// in the graphQL query names generated by the entity definitions.
        /// This test declares entities with the following graphQL definitions
        /// "book_alt"{
        ///     "graphQL": {
        ///         "type": {
        ///             "singular": "book",
        ///             "plural": "books"
        ///         }
        ///     }
        ///  }
        /// "book": {
        ///     "graphQL": true
        /// }
        /// </summary>
        [TestMethod]
        public void ValidateEntitesWithNameCollisionInSingularPluralTypeGeneratesDuplicateQueries()
        {
            SortedDictionary<string, Entity> entityCollection = new();

            // Entity Name: book_alt.
            // pk_query: book_by_pk
            // List query: books
            Entity book_alt = GraphQLTestHelpers.GenerateEntityWithSingularPlural("book", "books");

            // Entity Name: book
            // pk_query: book_by_pk
            // List Query: books
            Entity book = GraphQLTestHelpers.GenerateEmptyEntity();
            book.GraphQL = new GraphQLEntitySettings(true);

            entityCollection.Add("book_alt", book_alt);
            entityCollection.Add("book", book);
            ValidateExceptionForDuplicateQueriesDueToEntityDefinitions(entityCollection, "book_alt");
        }

        /// <summary>
        /// Validates that no exception is thrown when the entities
        /// exposed for graphQL generate unique queries.
        /// This test uses the following entity definitions
        /// "Book": {
        ///     "graphQL": false
        /// }
        /// "book": {
        ///     "graphQL": true
        /// }
        /// "book_alt"{
        ///     "graphQL": {
        ///         "type": {
        ///             "singular": "book_alt",
        ///             "plural": "books_alt"
        ///         }
        ///     }
        ///  }
        /// "Book_alt"{
        ///     "graphQL": {
        ///         "type": "book_alternative"
        ///     }
        /// }
        /// "BooK"{
        ///     graphQL: {
        ///         "type": {
        ///             "singular": "BooK",
        ///             "plural": "BooKs"
        ///         }
        ///     }
        ///  }
        /// "BOOK"{
        ///     graphQL: {
        ///         "type": {
        ///             "singular": "BOOK",
        ///             "plural": "BOOKS"
        ///         }
        ///     }
        ///  }
        /// </summary>
        [TestMethod]
        public void ValidateValidEntityDefinitionsDoesNotGenerateDuplicateQueries()
        {
            SortedDictionary<string, Entity> entityCollection = new();

            // Entity Name: Book
            // GraphQL is not exposed for this entity
            Entity bookWithUpperCase = GraphQLTestHelpers.GenerateEmptyEntity();

            // Entity Name: book
            // pk query: book_by_pk
            // List query: books
            Entity book = GraphQLTestHelpers.GenerateEmptyEntity();
            book.GraphQL = new GraphQLEntitySettings(true);

            // Entity Name: book_alt
            // pk_query: book_alt_by_pk
            // List query: books_alt
            Entity book_alt = GraphQLTestHelpers.GenerateEntityWithSingularPlural("book_alt", "books_alt");

            // Entity Name: Book_alt
            // pk_query: book_alternative
            // List query: books_alternatives
            Entity book_alt_upperCase = GraphQLTestHelpers.GenerateEntityWithStringType("book_alternative");

            // Entity Name: BooK
            // pk_query: booK_by_pk
            // List query: booKs
            Entity bookWithDifferentCase = GraphQLTestHelpers.GenerateEntityWithSingularPlural("BooK", "BooKs");

            // Entity Name: BOOK
            // pk_query: bOOK_by_pk
            // List query: bOOKS
            Entity bookWithAllUpperCase = GraphQLTestHelpers.GenerateEntityWithSingularPlural("BOOK", "BOOKS");

            entityCollection.Add("book", book);
            entityCollection.Add("Book", bookWithUpperCase);
            entityCollection.Add("book_alt", book_alt);
            entityCollection.Add("BooK", bookWithDifferentCase);
            entityCollection.Add("BOOK", bookWithAllUpperCase);
            entityCollection.Add("Book_alt", book_alt_upperCase);
            RuntimeConfigValidator.ValidateEntitiesDoNotGenerateDuplicateQueriesOrMutation(entityCollection);
        }

        /// <summary>
        /// Tests whether conflicting global REST/GraphQL fail config validation.
        /// </summary>
        /// <param name="graphQLConfiguredPath">GraphQL global path</param>
        /// <param name="restConfiguredPath">REST global path</param>
        /// <param name="expectError">Exception expected</param>
        [DataTestMethod]
        [DataRow("/graphql", "/graphql", true)]
        [DataRow("/api", "/api", true)]
        [DataRow("/graphql", "/api", false)]
        public void TestGlobalRouteValidation(string graphQLConfiguredPath, string restConfiguredPath, bool expectError)
        {
            Dictionary<GlobalSettingsType, object> settings = new()
            {
                { GlobalSettingsType.GraphQL, JsonSerializer.SerializeToElement(new GraphQLGlobalSettings(){ Path = graphQLConfiguredPath, AllowIntrospection = true }) },
                { GlobalSettingsType.Rest, JsonSerializer.SerializeToElement(new RestGlobalSettings(){ Path = restConfiguredPath }) }

            };

            RuntimeConfig configuration = ConfigurationTests.InitMinimalRuntimeConfig(globalSettings: settings, dataSource: new(DatabaseType.mssql));
            string expectedErrorMessage = "Conflicting GraphQL and REST path configuration.";

            try
            {
                RuntimeConfigValidator.ValidateGlobalEndpointRouteConfig(configuration);

                if (expectError)
                {
                    Assert.Fail(message: "Global endpoint route config validation expected to fail.");
                }
            }
            catch (DataApiBuilderException ex)
            {
                if (expectError)
                {
                    Assert.AreEqual(expected: HttpStatusCode.ServiceUnavailable, actual: ex.StatusCode);
                    Assert.AreEqual(expected: expectedErrorMessage, actual: ex.Message);
                }
                else
                {
                    Assert.Fail(message: "Global endpoint route config validation not expected to fail.");
                }
            }
        }

        /// <summary>
        /// A test helper method to validate the exception thrown when entities generate
        /// queries with the same name.
        /// </summary>
        /// <param name="entityCollection">Entity definitions</param>
        /// <param name="entityName">Entity name to construct the expected exception message</param>
        private static void ValidateExceptionForDuplicateQueriesDueToEntityDefinitions(SortedDictionary<string, Entity> entityCollection, string entityName)
        {
            DataApiBuilderException dabException = Assert.ThrowsException<DataApiBuilderException>(
               action: () => RuntimeConfigValidator.ValidateEntitiesDoNotGenerateDuplicateQueriesOrMutation(entityCollection));

            Assert.AreEqual(expected: $"Entity {entityName} generates queries/mutation that already exist", actual: dabException.Message);
            Assert.AreEqual(expected: HttpStatusCode.ServiceUnavailable, actual: dabException.StatusCode);
            Assert.AreEqual(expected: DataApiBuilderException.SubStatusCodes.ConfigValidationError, actual: dabException.SubStatusCode);
        }

        /// <summary>
        /// Method to create a sample entity with GraphQL enabled,
        /// with given source and relationship Info.
        /// </summary>
        /// <param name="source">Database name of entity.</param>
        /// <param name="relationshipMap">Dictionary containing {relationshipName, Relationship}</param>
        private static Entity GetSampleEntityUsingSourceAndRelationshipMap(
            string source,
            Dictionary<string, Relationship> relationshipMap,
            object graphQLdetails
            )
        {
            PermissionOperation actionForRole = new(
                Name: Config.Operation.Create,
                Fields: null,
                Policy: null);

            PermissionSetting permissionForEntity = new(
                role: "anonymous",
                operations: new object[] { JsonSerializer.SerializeToElement(actionForRole) });

            Entity sampleEntity = new(
                Source: JsonSerializer.SerializeToElement(source),
                Rest: null,
                GraphQL: graphQLdetails,
                Permissions: new PermissionSetting[] { permissionForEntity },
                Relationships: relationshipMap,
                Mappings: null
                );

            return sampleEntity;
        }

        /// <summary>
        /// Returns Dictionary containing pair of string and entity.
        /// It creates two sample entities and forms relationship between them.
        /// </summary>
        /// <param name="source">Database name of entity.</param>
        /// <param name="relationshipMap">Dictionary containing {relationshipName, Relationship}</param>
        private static Dictionary<string, Entity> GetSampleEntityMap(
            string sourceEntity,
            string targetEntity,
            string[] sourceFields,
            string[] targetFields,
            string linkingObject,
            string[] linkingSourceFields,
            string[] linkingTargetFields
        )
        {
            Dictionary<string, Relationship> relationshipMap = new();

            // Creating relationship between source and target entity.
            Relationship sampleRelationship = new(
                Cardinality: Cardinality.One,
                TargetEntity: targetEntity,
                SourceFields: sourceFields,
                TargetFields: targetFields,
                LinkingObject: linkingObject,
                LinkingSourceFields: linkingSourceFields,
                LinkingTargetFields: linkingTargetFields
            );

            relationshipMap.Add("rname1", sampleRelationship);

            Entity sampleEntity1 = GetSampleEntityUsingSourceAndRelationshipMap(
                source: "TEST_SOURCE1",
                relationshipMap: relationshipMap,
                graphQLdetails: true
            );

            sampleRelationship = new(
                Cardinality: Cardinality.One,
                TargetEntity: sourceEntity,
                SourceFields: targetFields,
                TargetFields: sourceFields,
                LinkingObject: linkingObject,
                LinkingSourceFields: linkingTargetFields,
                LinkingTargetFields: linkingSourceFields
            );

            relationshipMap = new();
            relationshipMap.Add("rname2", sampleRelationship);

            Entity sampleEntity2 = GetSampleEntityUsingSourceAndRelationshipMap(
                source: "TEST_SOURCE2",
                relationshipMap: relationshipMap,
                graphQLdetails: true
            );

            Dictionary<string, Entity> entityMap = new();
            entityMap.Add(sourceEntity, sampleEntity1);
            entityMap.Add(targetEntity, sampleEntity2);

            return entityMap;
        }

        /// <summary>
        /// Tests whether the REST path prefix is well formed or not.
        /// </summary>
        /// <param name="restPathPrefix">REST path prefix</param>
        /// <param name="expectedErrorMessage">Expected error message in case an exception is thrown.</param>
        /// <param name="expectError">Exception expected</param>
        // @"[\.:\?#/\[\]@!$&'()\*\+,;=]+";
        [DataTestMethod]
        [DataRow("/.", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character .")]
        [DataRow("/:", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character :")]
        [DataRow("/?", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character ?")]
        [DataRow("/#", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character #")]
        [DataRow("//", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character /")]
        [DataRow("/[", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character [")]
        [DataRow("/)", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character )")]
        [DataRow("/@", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character @")]
        [DataRow("/!", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character !")]
        [DataRow("/$", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character $")]
        [DataRow("/&", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character &")]
        [DataRow("/'", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character '")]
        [DataRow("/+", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character +")]
        [DataRow("/;", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character .")]
        [DataRow("/=", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved character .")]
        [DataRow("/?#*(=", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing multiple reserved characters /?#*(=")]
        [DataRow("/+&,", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved characters /+&,")]
        [DataRow("/@)", RuntimeConfigValidator.BADLY_FORMED_REST_PATH_ERR_MSG, true,
            DisplayName = "Rest path prefix containing reserved characters /@)")]
        [DataRow("", "REST path prefix cannot be null or empty.", true,
            DisplayName = "Empty Rest path prefix.")]
        [DataRow(null, "REST path prefix cannot be null or empty.", true,
            DisplayName = "Null Rest path prefix.")]
        [DataRow("?", "REST path should start with a '/'.", true,
            DisplayName = "Rest path prefix not starting with forward slash.")]
        [DataRow("/-rest-api", null, false,
            DisplayName = "Rest path prefix containing hyphen (-)")]
        [DataRow("/rest api", null, false,
            DisplayName = "Rest path prefix containing space in between")]
        [DataRow("/ restapi", null, false,
            DisplayName = "Rest path prefix containing space at the start")]
        [DataRow("/ rest_api", null, false,
            DisplayName = "Rest path prefix containing space at the start and underscore in between.")]
        [DataRow("/", null, false,
            DisplayName = "Rest path containing only a forward slash.")]
        public void ValidateRestPathIsWellFormed(
            string restPathPrefix,
            string expectedErrorMessage,
            bool expectError)
        {
            Dictionary<GlobalSettingsType, object> settings = new()
            {
                { GlobalSettingsType.GraphQL, JsonSerializer.SerializeToElement(new GraphQLGlobalSettings()) },
                { GlobalSettingsType.Rest, JsonSerializer.SerializeToElement(new RestGlobalSettings(){ Path = restPathPrefix }) }

            };

            RuntimeConfig configuration =
                ConfigurationTests.InitMinimalRuntimeConfig(globalSettings: settings, dataSource: new(DatabaseType.mssql));

            if (expectError)
            {
                DataApiBuilderException ex =
                    Assert.ThrowsException<DataApiBuilderException>(() =>
                    RuntimeConfigValidator.ValidateRestPathForRelationalDbs(configuration));
                Assert.AreEqual(expectedErrorMessage, ex.Message);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
            }
            else
            {
                RuntimeConfigValidator.ValidateRestPathForRelationalDbs(configuration);
            }
        }

        /// <summary>
        /// Method to validate that the validations for include/exclude fields for an entity
        /// work as expected.
        /// </summary>
        /// <param name="databasePolicy">Database policy for a particular role/action combination for an entity.</param>
        /// <param name="isFieldsPresent">Boolean variable representing whether fields section is present in config.</param>
        /// <param name="includedFields">Fields that are accessible to user for the role/action combination.</param>
        /// <param name="excludedFields">Fields that are inaccessible to user for the role/action combination.</param>
        /// <param name="exceptionExpected">Whether an exception is expected (true when validation fails).</param>
        [DataTestMethod]
        [DataRow(@"""@item.id ne 140""", true, "[]", @"[ ""name"" ]", true,
            DisplayName = "Empty array for included fields and db policy referencing some field.")]
        [DataRow(@"""""", true, "[]", @"[ ""name"" ]", false,
            DisplayName = "Empty array for included fields and empty db policy.")]
        [DataRow(@"""@item.id ne @claims.userId and @item.name eq @claims.userDetails""", true, @"[ ""id"", ""name"" ]", @"[ ""title"" ]", false,
            DisplayName = "All fields referenced by db policy present in included.")]
        [DataRow(@"""@item.id ne @claims.userId and @item.name eq @claims.userDetails""", true, @"[ ""id"" ]", @"[""name""]", true,
            DisplayName = "One field referenced by db policy present in excluded.")]
        [DataRow(@"""@item.id ne @claims.userId and @item.name eq @claims.userDetails""", true, @"[]", @"[]", true,
            DisplayName = "Empty arrays for included/excluded fields and non-empty database policy.")]
        [DataRow(@"""@item.id ne @claims.userId and @item.name eq @claims.userDetails""", true, null, null, false,
            DisplayName = "NULL included/excluded fields and non-empty database policy.")]
        [DataRow(@"""@item.id ne @claims.userId and @item.name eq @claims.userDetails""", true, null,
            @"[ ""id"", ""name"" ]", true,
            DisplayName = "NULL included fields and fields referenced in database policy are excluded.")]
        [DataRow(@"""@item.id ne @claims.userId and @item.name eq @claims.userDetails""", true, null,
            @"[""title""]", false,
            DisplayName = "NULL included fields and fields referenced in database policy are not excluded.")]
        [DataRow(@"""@item.id ne @claims.userId and @item.name eq @claims.userDetails""", true, @"[ ""*"" ]",
            null, false, DisplayName = "NULL excluded fields and fields referenced in database policy are included via wildcard")]
        [DataRow(@"""@item.id ne @claims.userId and @item.name eq @claims.userDetails""", false, null,
            null, false,
            DisplayName = "fields section absent.")]
        public void TestFieldInclusionExclusion(
            string databasePolicy,
            bool isFieldsPresent,
            string includedFields,
            string excludedFields,
            bool exceptionExpected)
        {

            string fields = string.Empty;

            if (isFieldsPresent)
            {
                string prefix = @",""fields"": {";
                string includeFields = includedFields is null ? string.Empty : @"""include"" : " + includedFields;
                string joinIncludeExclude = includedFields is not null && excludedFields is not null ? "," : string.Empty;
                string excludeFields = excludedFields is null ? string.Empty : @"""exclude"" : " + excludedFields;
                string postfix = "}";
                fields = prefix + includeFields + joinIncludeExclude + excludeFields + postfix;
            }

            string runtimeConfigString = @"{
                    " +
                @"""$schema"": ""test_schema""," +
                @"""data-source"": {
                    ""database-type"": ""mssql"",
                    ""connection-string"": ""testconnectionstring"",
                    ""options"":{
                        ""set-session-context"": false
                    }
                },
                ""runtime"": {
                    ""host"": {
                    ""mode"": ""development"",
                    ""authentication"": {
                        ""provider"": ""StaticWebApps""
                    }
                  }
                },
                ""entities"": {
                    ""Publisher"":{
                        ""source"": ""publishers"",
                        ""permissions"": [
                           {
                            ""role"": ""anonymous"",
                            ""actions"": [
                               {
                                ""action"": ""Read"",
                                ""policy"": {
                                    ""database"":" + databasePolicy +
                                  @"}" + fields +
                               @"}
                            ]
                           }
                         ]
                        }
                    }
                }";

            RuntimeConfig runtimeConfig = JsonSerializer.Deserialize<RuntimeConfig>(runtimeConfigString, RuntimeConfig.SerializerOptions);
            runtimeConfig!.DetermineGlobalSettings();
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // Perform validation on the permissions in the config and assert the expected results.
            if (exceptionExpected)
            {
                DataApiBuilderException ex =
                    Assert.ThrowsException<DataApiBuilderException>(() => configValidator.ValidatePermissionsInConfig(runtimeConfig));
                Assert.AreEqual("Not all the columns required by policy are accessible.", ex.Message);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
            }
            else
            {
                configValidator.ValidatePermissionsInConfig(runtimeConfig);
            }
        }

        /// <summary>
        /// Method to validate that any field set (included/excluded) if misconfigured, thorws an exception during
        /// config validation stage. If any field set contains a wildcard and any other field, we consider it as misconfigured.
        /// </summary>
        /// <param name="databasePolicy">Database policy for a particular role/action combination for an entity.</param>
        /// <param name="includedFields">Fields that are accessible to user for the role/action combination.</param>
        /// <param name="excludedFields">Fields that are inaccessible to user for the role/action combination.</param>
        /// <param name="exceptionExpected">Whether an exception is expected (true when validation fails).</param>
        /// <param name="misconfiguredColumnSet">Name of the misconfigured column set (included/excluded).</param>
        [DataTestMethod]
        [DataRow(@"""@item.id ne 140""", @"[ ""*"", ""id"" ]", @"[ ""name"" ]", true, "included",
            DisplayName = "Included fields containing wildcard and another field.")]
        [DataRow(@"""@item.id ne 140""", @"[ ""*"", ""id"" ]", @"[ ""*"", ""name"" ]", true, "excluded",
            DisplayName = "Excluded fields containing wildcard and another field.")]
        [DataRow(@"""@item.id ne 140""", null, @"[ ""*"", ""name"" ]", true, "excluded",
            DisplayName = "Excluded fields containing wildcard and another field and included fields is null.")]
        [DataRow(@"""@item.id ne 140""", @"[ ""*"", ""name"" ]", null, true, "included",
            DisplayName = "Included fields containing wildcard and another field and excluded fields is null.")]
        [DataRow(@"""@item.id ne 140""", @"[ ""*"" ]", @"[ ""name"" ]", false, null,
            DisplayName = "Well configured include/exclude fields.")]
        public void ValidateMisconfiguredColumnSets(
            string databasePolicy,
            string includedFields,
            string excludedFields,
            bool exceptionExpected,
            string misconfiguredColumnSet)
        {

            string fields = string.Empty;

            string prefix = @",""fields"": {";
            string includeFields = includedFields is null ? string.Empty : @"""include"" : " + includedFields;
            string joinIncludeExclude = includedFields is not null && excludedFields is not null ? "," : string.Empty;
            string excludeFields = excludedFields is null ? string.Empty : @"""exclude"" : " + excludedFields;
            string postfix = "}";
            fields = prefix + includeFields + joinIncludeExclude + excludeFields + postfix;

            string runtimeConfigString = @"{
                    " +
                @"""$schema"": ""test_schema""," +
                @"""data-source"": {
                    ""database-type"": ""mssql"",
                    ""connection-string"": ""testconnectionstring"",
                    ""options"":{
                        ""set-session-context"": false
                    }
                },
                ""runtime"": {
                    ""host"": {
                    ""mode"": ""development"",
                    ""authentication"": {
                        ""provider"": ""StaticWebApps""
                    }
                  }
                },
                ""entities"": {
                    ""Publisher"":{
                        ""source"": ""publishers"",
                        ""permissions"": [
                           {
                            ""role"": ""anonymous"",
                            ""actions"": [
                               {
                                ""action"": ""Read"",
                                ""policy"": {
                                    ""database"":" + databasePolicy +
                                  @"}" + fields +
                               @"}
                            ]
                           }
                         ]
                        }
                    }
                }";

            RuntimeConfig runtimeConfig = JsonSerializer.Deserialize<RuntimeConfig>(runtimeConfigString, RuntimeConfig.SerializerOptions);
            runtimeConfig!.DetermineGlobalSettings();
            RuntimeConfigValidator configValidator = AuthenticationConfigValidatorUnitTests.GetMockConfigValidator(ref runtimeConfig);

            // Perform validation on the permissions in the config and assert the expected results.
            if (exceptionExpected)
            {
                DataApiBuilderException ex =
                    Assert.ThrowsException<DataApiBuilderException>(() => configValidator.ValidatePermissionsInConfig(runtimeConfig));
                Assert.AreEqual($"No other field can be present with wildcard in the {misconfiguredColumnSet} " +
                    $"set for: entity:Publisher, role:anonymous, action:Read", ex.Message);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
                Assert.AreEqual(DataApiBuilderException.SubStatusCodes.ConfigValidationError, ex.SubStatusCode);
            }
            else
            {
                configValidator.ValidatePermissionsInConfig(runtimeConfig);
            }
        }
    }
}
