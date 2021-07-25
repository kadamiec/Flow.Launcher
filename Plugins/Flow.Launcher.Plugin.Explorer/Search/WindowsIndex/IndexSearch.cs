using Flow.Launcher.Plugin.Explorer.Search.QuickAccessLinks;
using Microsoft.Search.Interop;
using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.Explorer.Search.WindowsIndex
{
    internal static class IndexSearch
    {

        // Reserved keywords in oleDB
        private const string reservedStringPattern = @"^[`\@\#\^,\&\/\\\$\%_]+$";

        internal static async Task<List<Result>> ExecuteWindowsIndexSearchAsync(string indexQueryString, string connectionString, Query query, CancellationToken token)
        {
            var results = new List<Result>();
            var fileResults = new List<Result>();

            try
            {
                await using var conn = new OleDbConnection(connectionString);
                await conn.OpenAsync(token);
                token.ThrowIfCancellationRequested();

                await using var command = new OleDbCommand(indexQueryString, conn);
                // Results return as an OleDbDataReader.
                await using var dataReaderResults = await command.ExecuteReaderAsync(token) as OleDbDataReader;
                token.ThrowIfCancellationRequested();

                if (dataReaderResults.HasRows)
                {
                    while (await dataReaderResults.ReadAsync(token))
                    {
                        token.ThrowIfCancellationRequested();
                        if (dataReaderResults.GetValue(0) != DBNull.Value && dataReaderResults.GetValue(1) != DBNull.Value)
                        {
                            // # is URI syntax for the fragment component, need to be encoded so LocalPath returns complete path   
                            var encodedFragmentPath = dataReaderResults
                                .GetString(1)
                                .Replace("#", "%23", StringComparison.OrdinalIgnoreCase);

                            var path = new Uri(encodedFragmentPath).LocalPath;

                            if (dataReaderResults.GetString(2) == "Directory")
                            {
                                results.Add(ResultManager.CreateFolderResult(
                                    dataReaderResults.GetString(0),
                                    path,
                                    path,
                                    query, 0, true, true));
                            }
                            else
                            {
                                fileResults.Add(ResultManager.CreateFileResult(path, query, 0, true, true));
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // return empty result when cancelled
                return results;
            }
            catch (InvalidOperationException e)
            {
                // Internal error from ExecuteReader(): Connection closed.
                LogException("Internal error from ExecuteReader()", e);
            }
            catch (Exception e)
            {
                LogException("General error from performing index search", e);
            }

            results.AddRange(fileResults);

            // Intial ordering, this order can be updated later by UpdateResultView.MainViewModel based on history of user selection.
             return results;
        }

        internal async static Task<List<Result>> WindowsIndexSearchAsync(string searchString, 
                                                                  Func<CSearchQueryHelper> queryHelper,
                                                                  Func<string, string> constructQuery,
                                                                  List<AccessLink> exclusionList,
                                                                  Query query,
                                                                  CancellationToken token)
        {
            var regexMatch = Regex.Match(searchString, reservedStringPattern);

            if (regexMatch.Success)
                return new List<Result>();
            
            try
            {
                var constructedQuery = constructQuery(searchString);

                return RemoveResultsInExclusionList(
                        await ExecuteWindowsIndexSearchAsync(constructedQuery, queryHelper().ConnectionString, query, token).ConfigureAwait(false),
                        exclusionList,
                        token);
            }
            catch (COMException)
            {
                // Occurs because the Windows Indexing (WSearch) is turned off in services and unable to be used by Explorer plugin
                return new List<Result>
                {
                    new Result
                    {
                        Title = SearchManager.Context.API.GetTranslation("plugin_explorer_windowsSearchServiceNotRunning"),
                        SubTitle = SearchManager.Context.API.GetTranslation("plugin_explorer_windowsSearchServiceFix"),
                        IcoPath = Constants.ExplorerIconImagePath
                    }                    
                };
            }
        }

        private static List<Result> RemoveResultsInExclusionList(List<Result> results, List<AccessLink> exclusionList, CancellationToken token)
        {
            var indexExclusionListCount = exclusionList.Count;

            if (indexExclusionListCount == 0)
                return results;

            var filteredResults = new List<Result>();

            for (var index = 0; index < results.Count; index++)
            {
                token.ThrowIfCancellationRequested();

                var excludeResult = false;

                for (var i = 0; i < indexExclusionListCount; i++)
                {
                    token.ThrowIfCancellationRequested();

                    if (results[index].SubTitle.StartsWith(exclusionList[i].Path, StringComparison.OrdinalIgnoreCase))
                    {
                        excludeResult = true;
                        break;
                    }
                }

                if (!excludeResult)
                    filteredResults.Add(results[index]);
            }

            return filteredResults;
        }

        internal static bool PathIsIndexed(string path)
        {
            try
            {
                var csm = new CSearchManager();
                var indexManager = csm.GetCatalog("SystemIndex").GetCrawlScopeManager();
                return indexManager.IncludedInCrawlScope(path) > 0;
            }
            catch(COMException)
            {
                // Occurs because the Windows Indexing (WSearch) is turned off in services and unable to be used by Explorer plugin
                return false;
            }
        }

        private static void LogException(string message, Exception e)
        {
#if DEBUG // Please investigate and handle error from index search
            throw e;
#else
            Log.Exception($"|Flow.Launcher.Plugin.Explorer.IndexSearch|{message}", e);
#endif            
        }
    }
}
