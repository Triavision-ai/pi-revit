/** Compose the extension-only retry input after the bridge has added document targeting. */
export function publicBridgeSchema(parameters: unknown) {
	const schema = structuredClone(parameters ?? { type: "object", properties: {} }) as { properties?: Record<string, unknown> };
	schema.properties = { ...schema.properties, _operation_id: {
		type: "string", description: "Optional exact operation ID for retrying an identical earlier request. Reuses its result without repeating the action. Omit for a new operation.",
	} };
	return schema;
}
