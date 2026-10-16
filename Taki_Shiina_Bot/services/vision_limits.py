"""Limits shared by image intake, durable turns and Gemini inline requests."""

# Gemini's inline request includes base64 data, prompt and protocol envelope.
# Fourteen million raw bytes leave headroom below its 20 MB request limit.
MAX_INLINE_IMAGE_BYTES = 14_000_000
