# Development Workflow Instructions

## Core Principle: Incremental Development Within One Approved Numbered Slice

Work through one approved numbered implementation slice at a time, using small, focused, independently reviewable changes.

- Complete the approved slice through small RED -> GREEN cycles; do not implement all behaviors in one undifferentiated pass.
- Continue directly from each successful GREEN to the next behavior within the same approved slice, without an approval pause.
- Preserve a compilable solution at the end of each implementation step.
- Follow approved architecture, requirements, and design decisions.
- Do not add unrelated functionality, speculative abstractions, or unnecessary infrastructure.
- Do not automatically commit, push, merge, or accept changes.

## Test-Driven Development

The developer review checkpoint is completion of the entire numbered implementation slice, not each GREEN or internal milestone.

### RED: One Focused Behavior

1. Identify the next behavior within the approved numbered slice.
2. Write the smallest meaningful failing test or tests.
3. Run the tests and verify they fail for the expected reason.
4. Report the tests, expected behavior, and observed failure.
5. Continue directly to GREEN.

### GREEN: Minimum Implementation

1. Implement the minimum production code necessary to make the tests pass.
2. Run targeted tests and relevant regression tests.
3. Verify the implementation satisfies the approved behavior.
4. Continue to the next behavior within the same approved slice without waiting for approval.

### REFACTOR: Within the Approved Slice

1. Refactor as appropriate within the approved slice without changing approved behavior or expanding scope.
2. Rerun relevant tests after refactoring.
3. No separate approval is required for refactoring within that slice.

Refactoring is optional. Do not manufacture refactoring work when the implementation is already clear and maintainable.

### Numbered Slice Completion and Developer Review

1. Complete all required acceptance tests for the numbered slice and run relevant regression tests.
2. Report changed files, actual test commands and results, deviations, and unresolved issues.
3. STOP after the entire numbered slice is complete and wait for explicit developer approval before starting the next numbered slice.

For Slice 4, milestones 4a through 4f are implementation milestones within one numbered slice, not separate developer approval gates. Work through them using small RED -> GREEN cycles under the same Slice 4 approval.

## Review and Approval Rules

- Never interpret silence as approval.
- Never automatically proceed to the next numbered slice.
- If a material design contradiction or blocker requires a new decision, stop and ask rather than guessing. Do not change architecture or scope to work around it without approval.
- Distinguish tests actually executed from tests merely proposed.
- Do not claim tests pass without executing them.
- Keep the developer in control of scope, design, and implementation decisions.
- Approval to update documentation alone does not authorize implementation.

## Implementation Philosophy

Favor simple, production-conscious code over unnecessary complexity. Use established language and framework conventions, appropriate validation, error handling, and meaningful tests.

Keep all approved functionality, architectural decisions, and scope boundaries unchanged. The goal is correct, maintainable software developed through small, deliberate changes, with developer review at numbered-slice completion.
