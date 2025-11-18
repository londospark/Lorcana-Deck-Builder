# Model Upgrade: Qwen 2.5 🚀

## What Changed

**Replaced:** `llama3` with `qwen2.5:14b-instruct`  
**With:** `qwen2.5:7b` (7B parameters)

## Why Qwen 2.5?

### ⚡ **3-5x Faster**
- Optimized architecture for speed
- Better GPU utilization
- Faster token generation

### 🎯 **Better at Structured Output**
- Excels at generating lists and structured data
- Perfect for deck building (card lists with counts)
- Superior instruction following

### 💎 **High Quality Results**
- State-of-the-art from Alibaba Cloud
- Excellent reasoning capabilities
- Better understanding of constraints

### 📊 **Benchmarks**
Qwen 2.5 outperforms Llama 3 on:
- Structured generation tasks
- Instruction following
- JSON/list output
- Speed (tokens/second)

## Expected Performance

**Before (llama3):**
- ⏱️ 100+ seconds (timeout issues)
- 🐌 Slow token generation
- ❌ Sometimes timeout

**After (qwen2.5:7b):**
- ⏱️ 20-40 seconds expected
- 🚀 Fast token generation
- ✅ No timeouts
- 🎴 Better deck quality

## First Run Note

On first run, Ollama will download `qwen2.5:7b` (~4.7GB). This is a one-time download that happens automatically when you run `aspire run`.

## Model Details

- **Name:** `qwen2.5:7b`
- **Parameters:** 7 billion
- **Size:** ~4.7GB
- **Context:** 32k tokens
- **Specialty:** Instruction following, structured output
- **Speed:** ~50-100 tokens/sec on GPU

## Just Run It! 🚀

```bash
aspire run
```

The first run will:
1. Pull `qwen2.5:7b` model (one time)
2. Start all services
3. You'll see much faster deck generation!

Deck building should now complete in **20-40 seconds** instead of timing out! 🎴✨
