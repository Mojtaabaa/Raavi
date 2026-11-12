# B1 report — Data Model and Compatibility

**Date:** 2026-11-12  
**Status:** Complete

## Delivered

- Plan doc + ADR-056 amendments (Progress / ActualSpent / BlockComplete / one role per user).
- Migration `TaskEnterpriseB1Compatibility` (M1): three columns + unique index tighten with abort-on-duplicate (no silent deletes).
- Domain + TaskService + FE: progress, spent display, block-complete-children; complete returns `task.open_children` (409).
- Tests: prior TaskTests + parent-complete / one-role-per-user; FE tasks tests updated.

## Next

B2 Multiple Assignees — mostly shipped in M0/FE; harden remaining edge cases then B3 checklists/subtasks API.
