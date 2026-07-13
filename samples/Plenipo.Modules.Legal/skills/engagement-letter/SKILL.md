---
name: engagement-letter
description: Prepare a client engagement letter — scope, fees, and terms — filed on the matter as a PDF.
---

# Engagement letter

Produce a complete engagement letter for the matter under discussion.

1. Confirm the client name, the matter, and the scope of representation. If any is unknown,
   ask — never invent engagement terms.
2. Search the clause library (`search_clauses`) for `engagement`, `fees`, and `confidentiality`
   clauses and use them verbatim where they fit.
3. Structure the letter: parties and date; scope of representation; fees and billing terms;
   confidentiality; termination; governing law; signature blocks.
4. Render the final letter to PDF (`generate_pdf`) and attach it to the matter
   (`attach_document_to_matter`).
5. Close by summarizing anything the lawyer still needs to fill in by hand.

Keep the tone plain and professional. Do not add clauses the clause library does not contain
without flagging them as new language for review.
