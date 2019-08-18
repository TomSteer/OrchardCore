using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrchardCore.ContentManagement;
using OrchardCore.Entities;
using OrchardCore.Environment.Shell;
using OrchardCore.Indexing;
using OrchardCore.Lucene.Model;
using OrchardCore.Modules;
using OrchardCore.Settings;

namespace OrchardCore.Lucene
{
    /// <summary>
    /// This class provides services to update all the Lucene indices. It is non-rentrant so that calls 
    /// from different components can be done simultaneously, e.g. from a background task, an event or a UI interaction.
    /// It also indexes one content item at a time and provides the result to all indices.
    /// </summary>
    public class LuceneIndexingService
    {
        private const int BatchSize = 100;
        private readonly IShellHost _shellHost;
        private readonly ShellSettings _shellSettings;
        private readonly LuceneIndexingState _indexingState;
        private readonly LuceneIndexSettingsService _luceneIndexSettingsService;
        private readonly LuceneIndexManager _indexManager;
        private readonly IIndexingTaskManager _indexingTaskManager;
        private readonly ISiteService _siteService;

        public LuceneIndexingService(
            IShellHost shellHost,
            ShellSettings shellSettings,
            LuceneIndexingState indexingState,
            LuceneIndexSettingsService luceneIndexSettingsService,
            LuceneIndexManager indexManager,
            IIndexingTaskManager indexingTaskManager,
            ISiteService siteService,
            ILogger<LuceneIndexingService> logger)
        {
            _shellHost = shellHost;
            _shellSettings = shellSettings;
            _indexingState = indexingState;
            _luceneIndexSettingsService = luceneIndexSettingsService;
            _indexManager = indexManager;
            _indexingTaskManager = indexingTaskManager;
            _siteService = siteService;

            Logger = logger;
        }

        public ILogger Logger { get; }

        public async Task ProcessContentItemsAsync(string indexName = default)
        {
            // TODO: Lock over the filesystem in case two instances get a command to rebuild the index concurrently.
            var allIndices = new Dictionary<string, int>();
            var lastTaskId = Int32.MaxValue;
            IEnumerable<LuceneIndexSettings> indexSettingsList = null;


            if (String.IsNullOrEmpty(indexName))
            {
                indexSettingsList = _luceneIndexSettingsService.List();

                if (indexSettingsList == null)
                {
                    return;
                }

                // Find the lowest task id to process
                foreach (var indexSetting in indexSettingsList)
                {
                    var taskId = _indexingState.GetLastTaskId(indexSetting.IndexName);
                    lastTaskId = Math.Min(lastTaskId, taskId);
                    allIndices.Add(indexSetting.IndexName, taskId);
                }
            }
            else
            {
                indexSettingsList = _luceneIndexSettingsService.List().Where(x => x.IndexName == indexName);

                if (indexSettingsList == null)
                {
                    return;
                }

                var taskId = _indexingState.GetLastTaskId(indexName);
                lastTaskId = Math.Min(lastTaskId, taskId);
                allIndices.Add(indexName, taskId);
            }

            if (allIndices.Count == 0)
            {
                return;
            }

            var batch = Array.Empty<IndexingTask>();

            do
            {
                // Create a scope for the content manager
                var shellScope = await _shellHost.GetScopeAsync(_shellSettings);

                await shellScope.UsingAsync(async scope =>
                {
                    // Load the next batch of tasks
                    batch = (await _indexingTaskManager.GetIndexingTasksAsync(lastTaskId, BatchSize)).ToArray();

                    if (!batch.Any())
                    {
                        return;
                    }

                    var contentManager = scope.ServiceProvider.GetRequiredService<IContentManager>();
                    var indexHandlers = scope.ServiceProvider.GetServices<IContentItemIndexHandler>();

                    foreach (var indexSettings in indexSettingsList)
                    {
                        if (indexSettings.IndexedContentTypes.Length == 0)
                        {
                            continue;
                        }

                        var contentItems = await contentManager.GetAsync(batch.Select(x => x.ContentItemId), indexSettings.IndexLatest);

                        foreach (var task in batch)
                        {
                            var contentItem = contentItems.Where(x => x.ContentItemId == task.ContentItemId).FirstOrDefault();

                            if (contentItem == null)
                            {
                                continue;
                            }

                            // Ignore if this index has no content type setted to be indexed
                            if (!indexSettings.IndexedContentTypes.Contains(contentItem.ContentType))
                            {
                                continue;
                            }

                            var currentIndexTask = allIndices.Where(x => x.Key == indexSettings.IndexName).FirstOrDefault();

                            if (currentIndexTask.Value < task.Id)
                            {
                                _indexManager.DeleteDocuments(currentIndexTask.Key, new string[] { task.ContentItemId });
                            }

                            if (task.Type == IndexingTaskTypes.Update)
                            {
                                var context = new BuildIndexContext(new DocumentIndex(task.ContentItemId), contentItem, new string[] { contentItem.ContentType });

                                // Update the document from the index if its lastIndexId is smaller than the current task id. 
                                await indexHandlers.InvokeAsync(x => x.BuildIndexAsync(context), Logger);

                                if (currentIndexTask.Value < task.Id)
                                {
                                    _indexManager.StoreDocuments(currentIndexTask.Key, new DocumentIndex[] { context.DocumentIndex });
                                }
                            }
                        }
                    }

                    // Update task ids
                    lastTaskId = batch.Last().Id;

                    foreach (var index in allIndices)
                    {
                        if (index.Value < lastTaskId)
                        {
                            _indexingState.SetLastTaskId(index.Key, lastTaskId);
                        }
                    }

                    _indexingState.Update();

                });
            } while (batch.Length == BatchSize);
        }

        /// <summary>
        /// Creates a new index
        /// </summary>
        /// <returns></returns>
        public void CreateIndex(LuceneIndexSettings indexSettings)
        {
            _luceneIndexSettingsService.CreateIndex(indexSettings);
            RebuildIndex(indexSettings.IndexName);
        }

        /// <summary>
        /// Edit an existing index
        /// </summary>
        /// <returns></returns>
        public void EditIndex(LuceneIndexSettings indexSettings)
        {
            _luceneIndexSettingsService.EditIndex(indexSettings);
        }

        /// <summary>
        /// Deletes permanently an index
        /// </summary>
        /// <returns></returns>
        public void DeleteIndex(LuceneIndexSettings indexSettings)
        {
            _indexManager.DeleteIndex(indexSettings.IndexName);
            _luceneIndexSettingsService.DeleteIndex(indexSettings);
        }

        /// <summary>
        /// Restarts the indexing process from the beginning in order to update
        /// current content items. It doesn't delete existing entries from the index.
        /// </summary>
        public void ResetIndex(string indexName)
        {
            _indexingState.SetLastTaskId(indexName, 0);
            _indexingState.Update();
        }

        /// <summary>
        /// Deletes and recreates the full index content.
        /// </summary>
        public void RebuildIndex(string indexName)
        {
            _indexManager.DeleteIndex(indexName);
            _indexManager.CreateIndex(indexName);

            ResetIndex(indexName);
        }

        public async Task<LuceneSettings> GetLuceneSettingsAsync()
        {
            var siteSettings = await _siteService.GetSiteSettingsAsync();

            if (siteSettings.Has<LuceneSettings>())
            {
                return siteSettings.As<LuceneSettings>();
            }

            return null;
        }
    }
}