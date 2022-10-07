using System;
using System.Collections.Generic;
using System.Net;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.Models;
using Azure.DataApiBuilder.Service.Services;

namespace Azure.DataApiBuilder.Service.Resolvers
{
    ///<summary>
    /// Wraps all the required data and logic to write a SQL query resembling an UPSERT operation.
    ///</summary>
    public class SqlUpsertQueryStructure : BaseSqlQueryStructure
    {
        /// <summary>
        /// Names of columns that will be populated with values during the insert operation.
        /// </summary>
        public List<string> InsertColumns { get; }

        /// <summary>
        /// Values to insert into the given columns
        /// </summary>
        public List<string> Values { get; }

        /// <summary>
        /// Updates to be applied to selected row
        /// </summary>
        public List<Predicate> UpdateOperations { get; }

        /// <summary>
        /// The columns used for OUTPUT
        /// </summary>
        public List<LabelledColumn> OutputColumns { get; }

        /// <summary>
        /// Indicates whether the upsert should fallback to an update
        /// </summary>
        public bool IsFallbackToUpdate { get; private set; }

        /// <summary>
        /// Maps a column name to the created parameter name to avoid creating
        /// duplicate parameters. Useful in Upsert where an Insert and Update
        /// structure are both created.
        /// </summary>
        private Dictionary<string, string> ColumnToParam { get; }

        /// <summary>
        /// An upsert query must be prepared to be utilized for either an UPDATE or INSERT.
        ///
        /// </summary>
        /// <param name="tableName"></param>
        /// <param name="sqlMetadataProvider"></param>
        /// <param name="mutationParams"></param>
        /// <exception cref="DataApiBuilderException"></exception>
        public SqlUpsertQueryStructure(
            string entityName,
            ISqlMetadataProvider sqlMetadataProvider,
            IDictionary<string, object?> mutationParams,
            bool incrementalUpdate,
            string? baseEntityName = null,
            Dictionary<string, string>? columnAliases = null)
        : base(sqlMetadataProvider, entityName: entityName,
              baseEntityName: baseEntityName,
              columnAliases: columnAliases)
        {
            UpdateOperations = new();
            InsertColumns = new();
            Values = new();
            ColumnToParam = new();
            // All columns will be returned whether upsert results in UPDATE or INSERT
            OutputColumns = GenerateOutputColumns();

            TableDefinition tableDefinition = GetUnderlyingTableDefinition();
            SetFallbackToUpdateOnAutogeneratedPk(tableDefinition);

            // Populates the UpsertQueryStructure with UPDATE and INSERT column:value metadata
            PopulateColumns(mutationParams, tableDefinition, isIncrementalUpdate: incrementalUpdate);

            if (UpdateOperations.Count == 0)
            {
                throw new DataApiBuilderException(
                    message: "Update mutation does not update any values",
                    statusCode: HttpStatusCode.BadRequest,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
            }
        }

        /// <summary>
        /// Get the definition of a column by name
        /// </summary>
        public ColumnDefinition GetColumnDefinition(string columnName)
        {
            return GetUnderlyingTableDefinition().Columns[columnName];
        }

        private void PopulateColumns(
            IDictionary<string, object?> mutationParams,
            TableDefinition tableDefinition,
            bool isIncrementalUpdate)
        {
            List<string> primaryKeys = tableDefinition.PrimaryKey;
            List<string> primaryKeysInBaseTable = new();
            TableDefinition baseTableDefinition = SqlMetadataProvider.GetTableDefinition(BaseEntityName);
            List<string> schemaColumns = new();
            //= SqlMetadataProvider.GetTableDefinition(BaseEntityName).Columns.Keys.ToList();
            //primaryKeysInBaseTable.Add("publisher_id");
            foreach (string key in baseTableDefinition.Columns.Keys)
            {
                if (ColumnAliases.ContainsKey(key))
                {
                    schemaColumns.Add(ColumnAliases[key]);
                }
                else
                {
                    schemaColumns.Add(key);
                }

                if (baseTableDefinition.PrimaryKey.Contains(key))
                {
                    if (ColumnAliases.ContainsKey(key))
                    {
                        primaryKeysInBaseTable.Add(ColumnAliases[key]);
                    }
                    else
                    {
                        primaryKeysInBaseTable.Add(key);
                    }
                }
            }

            try
            {
                foreach (KeyValuePair<string, object?> param in mutationParams)
                {
                    // since we have already validated mutationParams we know backing column exists
                    SqlMetadataProvider.TryGetBackingColumn(EntityName, param.Key, out string? backingColumn);
                    // Create Parameter and map it to column for downstream logic to utilize.
                    string paramIdentifier;
                    if (param.Value != null)
                    {
                        paramIdentifier = MakeParamWithValue(GetParamAsColumnSystemType(param.Value.ToString()!, backingColumn!));
                    }
                    else
                    {
                        paramIdentifier = MakeParamWithValue(null);
                    }

                    ColumnToParam.Add(backingColumn!, paramIdentifier);

                    // Create a predicate for UPDATE Operation.
                    Predicate predicate = new(
                        new PredicateOperand(new Column(tableSchema: DatabaseObject.SchemaName, tableName: DatabaseObject.Name, columnName: backingColumn!)),
                        PredicateOperation.Equal,
                        new PredicateOperand($"@{paramIdentifier}")
                    );

                    // We are guaranteed by the RequestValidator, that a primary key column is in the URL, not body.
                    // That means we must add the PK as predicate for the update request,
                    // as Update request uses Where clause to target item by PK.
                    if (primaryKeys.Contains(backingColumn!))
                    {
                        if (primaryKeysInBaseTable.Contains(backingColumn!))
                        {
                            PopulateColumnsAndParams(backingColumn!);
                        }

                        // PK added as predicate for Update Operation
                        Predicates.Add(predicate);

                        // Track which columns we've acted upon,
                        // so we can add nullified remainder columns later.
                        schemaColumns.Remove(backingColumn!);
                    }
                    // No need to check param.key exists in schema as invalid columns are caught in RequestValidation.
                    else
                    {
                        // Update Operation. Add since mutation param is not a PK.
                        UpdateOperations.Add(predicate);
                        schemaColumns.Remove(backingColumn!);

                        // Insert Operation, create record with request specified value.
                        PopulateColumnsAndParams(backingColumn!);
                    }
                }

                if (!(DatabaseObject.ObjectType is SourceType.View))
                {
                    // Process remaining columns in schemaColumns.
                    if (isIncrementalUpdate)
                    {
                        SetFallbackToUpdateOnMissingColumInPatch(schemaColumns, tableDefinition);
                    }
                    else
                    {
                        // UpdateOperations will be modified and have nullable values added for update when appropriate
                        AddNullifiedUnspecifiedFields(schemaColumns, UpdateOperations, baseTableDefinition);
                    }
                }
            }
            catch (ArgumentException ex)
            {
                // ArgumentException thrown from GetParamAsColumnSystemType()
                throw new DataApiBuilderException(
                    message: ex.Message,
                    statusCode: HttpStatusCode.BadRequest,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
            }
        }

        /// <summary>
        /// Populates the column name in Columns, gets created parameter
        /// and adds its value to Values.
        /// </summary>
        /// <param name="columnName">The name of the column.</param>
        private void PopulateColumnsAndParams(string columnName)
        {
            InsertColumns.Add(columnName);
            string paramName;
            paramName = ColumnToParam[columnName];
            Values.Add($"@{paramName}");
        }

        /// <summary>
        /// Sets the value of fallback to update by checking if the pk of the table is autogenerated
        /// </summary>
        /// <param name="tableDef"></param>
        private void SetFallbackToUpdateOnAutogeneratedPk(TableDefinition tableDef)
        {
            bool pkIsAutogenerated = false;
            foreach (string primaryKey in tableDef.PrimaryKey)
            {
                if (tableDef.Columns[primaryKey].IsAutoGenerated)
                {
                    pkIsAutogenerated = true;
                    break;
                }
            }

            IsFallbackToUpdate = pkIsAutogenerated;
        }

        /// <summary>
        /// Sets the value of fallback to update by checking if any required column (non autogenerated, non default, non nullable)
        /// is missing during PATCH
        /// </summary>
        /// <param name="tableDef"></param>
        private void SetFallbackToUpdateOnMissingColumInPatch(List<string> leftoverSchemaColumns, TableDefinition tableDef)
        {
            foreach (string leftOverColumn in leftoverSchemaColumns)
            {
                if (!tableDef.Columns[leftOverColumn].IsAutoGenerated
                    && !tableDef.Columns[leftOverColumn].HasDefault
                    && !tableDef.Columns[leftOverColumn].IsNullable)
                {
                    IsFallbackToUpdate = true;
                    break;
                }
            }
        }
    }
}
