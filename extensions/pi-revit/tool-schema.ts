/** Compose the extension-only retry input after the bridge has added document targeting. */
export function publicBridgeSchema(parameters: unknown) {
	const schema = structuredClone(parameters ?? { type: "object", properties: {} }) as { properties?: Record<string, unknown>; required?: string[] };
	// The agent never handles the 65-character document identity: Pi fills it in from the latest
	// get_model_overview, and the bridge still requires and checks it.
	// The legacy title check (expected_document) adds nothing to the identity and is hidden as well; a
	// linked model's identity is filled in from get_linked_models by link_instance_id.
	const hidden = ["expected_document_id", "expected_document", "expected_linked_document_id"];
	if (schema.properties) {
		schema.properties = Object.fromEntries(Object.entries(schema.properties).filter(([name]) => !hidden.includes(name)));
		if (Array.isArray(schema.required)) schema.required = schema.required.filter(name => !hidden.includes(name));
	}
	schema.properties = { ...schema.properties, _operation_id: {
		type: "string", description: "Optional: the operation handle shown with an earlier result (for example op-3f9a2c), to retry an identical request; reuses its result without repeating the action. Omit for a new operation.",
	} };
	return schema;
}
