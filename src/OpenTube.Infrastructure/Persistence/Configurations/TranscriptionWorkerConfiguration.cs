using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTube.Domain.Entities;

namespace OpenTube.Infrastructure.Persistence.Configurations;

public class TranscriptionWorkerConfiguration : IEntityTypeConfiguration<TranscriptionWorker>
{
    public void Configure(EntityTypeBuilder<TranscriptionWorker> builder)
    {
        builder.ToTable("transcription_workers");
        builder.HasKey(w => w.WorkerId);
        builder.Property(w => w.WorkerId).HasMaxLength(200);
        builder.Property(w => w.Engine).HasMaxLength(200);
    }
}
