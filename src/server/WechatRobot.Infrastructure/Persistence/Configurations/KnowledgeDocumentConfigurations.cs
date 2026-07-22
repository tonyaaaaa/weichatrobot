using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocumentEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocumentEntity> builder)
    {
        builder.ToTable("knowledge_document");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Title).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.ActiveCollectionName).HasMaxLength(128);
        builder.Property(entity => entity.ActiveDistance).HasMaxLength(16);
        builder.Property(entity => entity.ActiveVersionId).IsConcurrencyToken();
        builder.HasIndex(entity => entity.Status);
    }
}

internal sealed class KnowledgeDocumentVersionConfiguration : IEntityTypeConfiguration<KnowledgeDocumentVersionEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocumentVersionEntity> builder)
    {
        builder.ToTable("knowledge_document_version");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.OriginalFileName).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.SafeFileName).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.ContentType).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.ObjectKey).HasMaxLength(1024).IsRequired();
        builder.Property(entity => entity.PublicUrl).HasMaxLength(2048);
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.Status).IsConcurrencyToken();
        builder.Property(entity => entity.PreviewRevision).IsConcurrencyToken();
        builder.Property(entity => entity.FailureReason).HasMaxLength(512);
        builder.Property(entity => entity.StagedContent).HasColumnType("longblob").IsRequired();
        builder.Property(entity => entity.IndexCollectionName).HasMaxLength(128);
        builder.Property(entity => entity.VectorDistance).HasMaxLength(16);
        builder.HasIndex(entity => entity.Sha256).IsUnique();
        builder.HasIndex(entity => new { entity.KnowledgeDocumentId, entity.Version }).IsUnique();
        builder.HasOne<KnowledgeDocumentEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeDocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunkEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunkEntity> builder)
    {
        builder.ToTable("knowledge_chunk");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Text).HasColumnType("longtext").IsRequired();
        builder.Property(entity => entity.HeadingsJson).HasColumnType("json").HasDefaultValueSql("(JSON_ARRAY())").IsRequired();
        builder.Property(entity => entity.SynonymsJson).HasColumnType("json").HasDefaultValueSql("(JSON_ARRAY())").IsRequired();
        builder.Property(entity => entity.Question).HasMaxLength(2048);
        builder.Property(entity => entity.Answer).HasColumnType("longtext");
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.HasIndex(entity => new { entity.KnowledgeDocumentVersionId, entity.Sequence }).IsUnique();
        builder.HasOne<KnowledgeDocumentVersionEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeDocumentVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class KnowledgeChunkPreviewConfiguration : IEntityTypeConfiguration<KnowledgeChunkPreviewEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunkPreviewEntity> builder)
    {
        builder.ToTable("knowledge_chunk_preview");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Text).HasColumnType("longtext").IsRequired();
        builder.Property(entity => entity.HeadingsJson).HasColumnType("json").IsRequired();
        builder.Property(entity => entity.SynonymsJson).HasColumnType("json").IsRequired();
        builder.Property(entity => entity.Question).HasMaxLength(2048);
        builder.Property(entity => entity.Answer).HasColumnType("longtext");
        builder.HasIndex(entity => new { entity.KnowledgeDocumentVersionId, entity.Sequence }).IsUnique();
        builder.HasOne<KnowledgeDocumentVersionEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeDocumentVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class KnowledgeChunkTagConfiguration : IEntityTypeConfiguration<KnowledgeChunkTagEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunkTagEntity> builder)
    {
        builder.ToTable("knowledge_chunk_tag");
        builder.HasKey(entity => new { entity.KnowledgeChunkId, entity.KnowledgeTagId });
        builder.HasOne<KnowledgeChunkEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeChunkId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<KnowledgeTagEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeTagId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class KnowledgeOcrPageConfiguration : IEntityTypeConfiguration<KnowledgeOcrPageEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeOcrPageEntity> builder)
    {
        builder.ToTable("knowledge_ocr_page");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired().IsConcurrencyToken();
        builder.Property(entity => entity.BlocksJson).HasColumnType("json").IsRequired();
        builder.Property(entity => entity.Error).HasMaxLength(512);
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(128).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.KnowledgeDocumentVersionId, entity.PageNumber }).IsUnique();
        builder.HasOne<KnowledgeDocumentVersionEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeDocumentVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class KnowledgeIndexJobConfiguration : IEntityTypeConfiguration<KnowledgeIndexJobEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeIndexJobEntity> builder)
    {
        builder.ToTable("knowledge_index_job");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Operation).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.PreviousActiveCollectionName).HasMaxLength(128);
        builder.Property(entity => entity.PreviousActiveDistance).HasMaxLength(16);
        builder.Property(entity => entity.CollectionName).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Distance).HasMaxLength(16).IsRequired();
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(128);
        builder.Property(entity => entity.FailureReason).HasMaxLength(1024);
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.Status, entity.NextAttemptAtUtc });
        builder.HasIndex(entity => new { entity.KnowledgeDocumentVersionId, entity.Operation, entity.Status });
        builder.HasOne<KnowledgeDocumentEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeDocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<KnowledgeDocumentVersionEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeDocumentVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}
