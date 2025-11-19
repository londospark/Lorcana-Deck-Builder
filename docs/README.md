# Documentation Index

Welcome to the Lorcana Deck Builder docs. Everything previously scattered in the repo root now lives here.

## ⚠️ CRITICAL: Documentation Standards for AI Agents

**All documentation files MUST be created in the `docs/` folder.**

- Never create `.md` files in the project root (except the main `README.md`)
- Use descriptive, uppercase filenames with underscores (e.g., `ASPIRE_MCP_WORKFLOW.md`)
- Check this folder first before creating new documentation
- Update this index when adding significant new documents

---

- Start here: Team/roles overview and collaboration notes in `./AGENTS.md`.
- Looking for the app overview and how to run? See the root `README.md`, then return here for deeper topics.

## Quick Links
- Project README: ../README.md
- Session Summary (latest): ./SESSION_SUMMARY.md

## Agentic + RAG
- ./AGENTIC_ONLY.md
- ./AGENTIC_RAG_PLAN.md
- ./AGENTIC_REDESIGN.md
- ./AGENTIC_STATUS.md
- ./RAG_WORKFLOW_STATUS.md
- ./ENHANCED_RAG_FILTERING.md
- ./PHASE2_EMBEDDING_UPGRADE.md
- ./MODEL_UPGRADE.md
- ./NATIVE_QDRANT_FILTERING.md
- ./ENHANCED_CARD_DATA.md

## UI / Styling
- ./UI_AUTOMATED.md
- ./UI_BLUE_THEME.md
- ./CUSTOM_DROPDOWNS.md
- ./LIQUID_GLASS.md
- ./BOLERO_FIX.md
- ./BUILD_BUTTON_FIX.md
- ./COLOR_SELECTION_BUG.md
- ./COLOR_SELECTION_FIX.md
- ./DECK_SIZE_AND_RULES_FIX.md

## Analysis / Formats
- ./LORCANA_COLOR_ANALYSIS.md
- ./INFINITE_FORMAT.md

## Operations / Ingestion / Cleanup
- ./ASPIRE_MCP_WORKFLOW.md (Development workflow - essential reading!)
- ./WORKER_INGESTION.md
- ./CLEANUP_COMPLETE.md
- ./LOGGING_AND_FIX_COMPLETE.md

## Team / Meta
- ./AGENTS.md
- ./SESSION_SUMMARY.md

## Conventions
- All markdowns in this folder are the canonical sources.
- If you find a duplicate at repo root, prefer the `docs/` version.
- Keep filenames concise and descriptive; use Title Case in headings.

## Suggested Reading Order
1) Development Workflow: ./ASPIRE_MCP_WORKFLOW.md (start here for build/test cycles!)
2) Agentic + RAG: ./AGENTIC_ONLY.md → ./AGENTIC_RAG_PLAN.md → ./AGENTIC_REDESIGN.md
3) UI / Styling: ./UI_AUTOMATED.md → ./UI_BLUE_THEME.md → ./CUSTOM_DROPDOWNS.md
4) Operations: ./WORKER_INGESTION.md → ./LOGGING_AND_FIX_COMPLETE.md
