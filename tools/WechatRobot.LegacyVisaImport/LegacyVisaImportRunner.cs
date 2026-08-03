namespace WechatRobot.LegacyVisaImport;

public sealed record LegacyVisaImportOptions(
    bool Apply,
    string OutputDirectory,
    string CheckpointPath,
    string RequiredTagName,
    TimeSpan IndexTimeout);

public sealed record LegacyVisaImportSummary(
    int Total,
    int Creates,
    int Updates,
    int Skips,
    int Applied);

public sealed class LegacyVisaImportRunner(
    KnowledgeApiClient api,
    LegacyVisaImportOptions options)
{
    public async Task<LegacyVisaImportSummary> RunAsync(
        IReadOnlyList<LegacyVisaProduct> products,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var checkpoint = await LegacyImportCheckpointStore.LoadAsync(
            options.CheckpointPath, cancellationToken);
        var tagId = LegacyVisaImportPlanner.ResolveTag(
            await api.GetTagOptionsAsync(cancellationToken), options.RequiredTagName);
        var documents = (await api.GetAllDocumentsAsync(cancellationToken)).ToList();
        var creates = 0;
        var updates = 0;
        var skips = 0;
        var applied = 0;
        var pendingIndexes = new List<PendingIndex>();
        var seenSourceHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedBySha = new Dictionary<string, LegacyImportCheckpointEntry>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var product in products)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = LegacyVisaMarkdownRenderer.Render(product, DateOnly.FromDateTime(DateTime.Today));
            await File.WriteAllTextAsync(
                Path.Combine(options.OutputDirectory, rendered.FileName),
                rendered.Markdown,
                cancellationToken);
            checkpoint.Entries.TryGetValue(product.LegacyVisaId, out var entry);
            var duplicate = LegacyVisaImportPlanner.ResolveSourceDuplicate(
                rendered, resolvedBySha);
            if (!seenSourceHashes.Add(rendered.Sha256))
            {
                skips++;
                Console.WriteLine(
                    $"{product.LegacyVisaId}\tsource-duplicate\t{rendered.FileName}");
                if (!options.Apply) continue;
                if (duplicate is null)
                    throw new InvalidOperationException(
                        "source_duplicate_checkpoint_missing");
                checkpoint.Entries[product.LegacyVisaId] = duplicate;
                await LegacyImportCheckpointStore.SaveAsync(
                    options.CheckpointPath, checkpoint, cancellationToken);
                if (duplicate.State == "indexing")
                {
                    pendingIndexes.Add(new(
                        product.LegacyVisaId,
                        rendered.Sha256,
                        duplicate.DocumentId,
                        duplicate.VersionId));
                    continue;
                }
                if (duplicate.State == "consistent") continue;
                throw new InvalidOperationException(
                    $"source_duplicate_stage_invalid:{duplicate.State}");
            }

            var decision = LegacyVisaImportPlanner.Decide(rendered, documents, entry);
            switch (decision.Action)
            {
                case "create": creates++; break;
                case "update": updates++; break;
                case "skip" or "resume-approved" or "resume-uploaded" or "resume-indexing": skips++; break;
            }

            Console.WriteLine($"{product.LegacyVisaId}\t{decision.Action}\t{rendered.FileName}");
            if (!options.Apply) continue;
            if (decision.Action == "skip")
            {
                resolvedBySha[rendered.Sha256] = entry
                    ?? throw new InvalidOperationException("skip_checkpoint_missing");
                continue;
            }

            Guid documentId;
            Guid versionId;
            string stage;
            if (decision.Action is "create" or "update")
            {
                var existingVersion = decision.DocumentId is { } existingDocumentId
                    ? (await api.GetVersionsAsync(existingDocumentId, cancellationToken))
                        .SingleOrDefault(version => string.Equals(
                            version.Sha256, rendered.Sha256, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (existingVersion is not null)
                {
                    documentId = decision.DocumentId!.Value;
                    versionId = existingVersion.Id;
                    stage = LegacyVisaImportPlanner.ResumeState(existingVersion.Status);
                }
                else
                {
                    var upload = await api.UploadAsync(rendered, decision.DocumentId, cancellationToken);
                    documentId = upload.DocumentId;
                    versionId = upload.VersionId;
                    stage = upload.State switch
                    {
                        "uploaded" => "uploaded",
                        "failed" => "failed",
                        _ => throw new InvalidOperationException($"upload_stage_invalid:{upload.State}")
                    };
                    if (decision.Action == "create")
                        documents.Add(new KnowledgeDocumentMatch(documentId, rendered.FileName));
                }
                checkpoint.Entries[product.LegacyVisaId] =
                    new(rendered.Sha256, documentId, versionId, stage);
                await LegacyImportCheckpointStore.SaveAsync(options.CheckpointPath, checkpoint, cancellationToken);
            }
            else
            {
                var resume = entry ?? throw new InvalidOperationException("resume_checkpoint_missing");
                documentId = resume.DocumentId;
                versionId = resume.VersionId;
                stage = resume.State;
            }

            if (stage == "failed")
            {
                var retry = await api.RetryUploadAsync(documentId, cancellationToken);
                if (retry.DocumentId != documentId || retry.VersionId != versionId)
                    throw new InvalidOperationException("retry_upload_identity_mismatch");
                stage = retry.State;
                checkpoint.Entries[product.LegacyVisaId] =
                    new(rendered.Sha256, documentId, versionId, stage);
                await LegacyImportCheckpointStore.SaveAsync(options.CheckpointPath, checkpoint, cancellationToken);
                if (stage == "failed")
                    throw new InvalidOperationException($"upload_retry_exhausted:{documentId:D}");
            }

            if (stage == "uploaded")
            {
                var previews = await WaitForPreviewsAsync(versionId, cancellationToken);
                await api.ApprovePreviewsAsync(versionId, previews.Revision, cancellationToken);
                stage = "approved";
                checkpoint.Entries[product.LegacyVisaId] =
                    new(rendered.Sha256, documentId, versionId, stage);
                await LegacyImportCheckpointStore.SaveAsync(options.CheckpointPath, checkpoint, cancellationToken);
            }

            if (stage == "approved")
            {
                await api.QueueIndexAsync(documentId, versionId, tagId, cancellationToken);
                stage = "indexing";
                checkpoint.Entries[product.LegacyVisaId] =
                    new(rendered.Sha256, documentId, versionId, stage);
                await LegacyImportCheckpointStore.SaveAsync(options.CheckpointPath, checkpoint, cancellationToken);
            }
            if (stage != "indexing")
                throw new InvalidOperationException($"resume_stage_invalid:{stage}");
            resolvedBySha[rendered.Sha256] = new(
                rendered.Sha256, documentId, versionId, stage);
            pendingIndexes.Add(new(
                product.LegacyVisaId, rendered.Sha256, documentId, versionId));
        }

        foreach (var pending in pendingIndexes)
        {
            await WaitForConsistencyAsync(
                pending.DocumentId, pending.VersionId, cancellationToken);
            checkpoint.Entries[pending.LegacyVisaId] =
                new(pending.Sha256, pending.DocumentId, pending.VersionId, "consistent");
            await LegacyImportCheckpointStore.SaveAsync(
                options.CheckpointPath, checkpoint, cancellationToken);
            applied++;
        }

        return new(products.Count, creates, updates, skips, applied);
    }

    private async Task WaitForConsistencyAsync(
        Guid documentId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + options.IndexTimeout;
        var recoveryAttempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var status = await api.GetIndexStatusAsync(documentId, cancellationToken);
                if (status.ActiveVersionId == versionId && status.Consistency == "consistent") return;

                var version = (await api.GetVersionsAsync(documentId, cancellationToken))
                    .Single(item => item.Id == versionId);
                var failedJob = version.IndexJobs?
                    .LastOrDefault(job => string.Equals(
                        job.Status, "failed", StringComparison.Ordinal));
                if (failedJob is not null)
                {
                    if (!LegacyVisaImportPlanner.CanRetryIndex(recoveryAttempts))
                        throw new InvalidOperationException(
                            $"index_retry_exhausted:{documentId:D}");
                    await api.RetryIndexAsync(failedJob.Id, cancellationToken);
                    recoveryAttempts++;
                }
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                // Qdrant outages are transient; remain within the bounded index deadline.
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException($"index_consistency_timeout:{documentId:D}");
    }

    private async Task<PreviewSet> WaitForPreviewsAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var previews = await api.GetPreviewsAsync(versionId, cancellationToken);
            if (previews.Items.Count > 0) return previews;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        throw new TimeoutException($"preview_generation_timeout:{versionId:D}");
    }

    private sealed record PendingIndex(
        string LegacyVisaId,
        string Sha256,
        Guid DocumentId,
        Guid VersionId);
}
