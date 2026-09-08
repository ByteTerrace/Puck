/*
    Shared ABAC role-assignment conditions.

    Grammar: a condition is an AND of clauses; each clause permits its listed data actions
    only when at least one of its branches holds. A clause with an empty branch list denies
    its actions outright. Any action a role grants that no clause mentions is unrestricted.

    Everything here is a function (not a variable) so the conditions remain usable inside
    other user-defined functions, which cannot reference variables.

    Rules for editing:
    - A condition is capped at 8 KB.
    - A missing attribute evaluates fail-closed. Encode any fail-open default explicitly
      with NOT Exists; never lean on missing-attribute behavior.
*/

func allOf(predicates string[]) string => join(predicates, ' AND ')
func blobAction(action string) string => 'ActionMatches{\'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/${action}\'}'
func blobListAction() string => '${blobAction('read')} AND SubOperationMatches{\'Blob.List\'}'
func blobPathIsNotUnder(prefix string) string => '@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringNotStartsWithIgnoreCase \'${prefix}/\''
func blobPathIsUnder(prefix string) string => '@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike \'${prefix}/*\''
func blobPathIsUnderAnyOf(prefixes string[]) string => '@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] ForAnyOfAnyValues:StringLike {${join(map(prefixes, prefix => '\'${prefix}/*\''), ', ')}}'
func blobReadAction() string => '${blobAction('read')} AND !SubOperationMatches{\'Blob.List\'}'
func clause(actions string[], branches string[]) string => '((${join(map(actions, action => '!(${action})'), ' AND ')})${(empty(branches) ? '' : ' OR ${join(map(branches, branch => '(${branch})'), ' OR ')}')})'
func containerMetadataIsNotFrozen() string => '((NOT Exists @Resource[Microsoft.Storage/storageAccounts/blobServices/containers/metadata:Frozen]) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/metadata:Frozen] StringNotEqualsIgnoreCase \'True\'))'
func isNotSystemContainer() string => 'NOT @Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringStartsWithIgnoreCase \'$\''
func isOwnContainer() string => '@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEqualsIgnoreCase ${principalObjectId()}'
func isOwnQueue() string => '@Resource[Microsoft.Storage/storageAccounts/queueServices/queues:name] StringEqualsIgnoreCase ${principalObjectId()}'
func isPublicPathOrOwnContainer() string => '((${blobPathIsUnder('public')}) OR (${isOwnContainer()}))'
func listPrefixIsUnderAnyOf(prefixes string[]) string => '@Request[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:prefix] ForAnyOfAnyValues:StringLike {${join(map(prefixes, prefix => '\'${prefix}/*\''), ', ')}}'
func principalObjectId() string => '@Principal[Microsoft.Directory/CustomSecurityAttributes/Id:ByteTerraceUsers_ObjectId]'
func queueMessageAction(action string) string => 'ActionMatches{\'Microsoft.Storage/storageAccounts/queueServices/queues/messages/${action}\'}'

@export()
func byteTerraceApiHostStorageUserCondition() string => join(
  [
    clause(
      [
        blobAction('add/action')
        blobAction('delete')
        blobAction('move/action')
        blobReadAction()
        blobAction('runAsSuperUser/action')
        blobAction('write')
      ],
      [allOf([isNotSystemContainer(), blobPathIsNotUnder('private')])]
    )
    clause([blobListAction()], [listPrefixIsUnderAnyOf(['system', 'public'])])
  ],
  ' AND '
)
@export()
func byteTerraceUserStorageUserCondition() string => join(
  [
    clause(
      [
        blobAction('add/action')
        blobAction('move/action')
        blobAction('runAsSuperUser/action')
        blobAction('write')
      ],
      [allOf([isNotSystemContainer(), isOwnContainer(), blobPathIsUnderAnyOf(['private', 'public']), containerMetadataIsNotFrozen()])]
    )
    // Deletes are separate from the other mutations because they must NOT be Frozen-gated: the
    // migration drain deletes from a frozen source, and a stray delete against an already-copied
    // source costs nothing worse than a briefly resurrected blob at the destination.
    clause(
      [blobAction('delete')],
      [allOf([isNotSystemContainer(), isOwnContainer(), blobPathIsUnderAnyOf(['private', 'public'])])]
    )
    clause(
      [blobReadAction()],
      [allOf([isNotSystemContainer(), isPublicPathOrOwnContainer(), blobPathIsUnderAnyOf(['private', 'public', 'system'])])]
    )
    clause([blobListAction()], [isOwnContainer()])
    // The role grants tags/read and tags/write (bootstrap.cs); without these clauses they would
    // be unrestricted — cross-tenant tag reads and tag tampering. Tag values carry no access
    // semantics — this is confidentiality and anti-tamper only. There is no
    // Blob.Write.WithTagHeaders clause: a tag-carrying upload still matches the plain add/write
    // actions, so the mutation clause above already gates it.
    clause(
      [blobAction('tags/read')],
      [isOwnContainer()]
    )
    clause(
      [blobAction('tags/write')],
      [allOf([isOwnContainer(), blobPathIsUnder('private'), containerMetadataIsNotFrozen()])]
    )
    clause(
      [
        queueMessageAction('read')
        queueMessageAction('write')
        queueMessageAction('delete')
        queueMessageAction('process/action')
      ],
      [isOwnQueue()]
    )
  ],
  ' AND '
)
@export()
func frontDoorPublicContentReadCondition() string => join(
  [
    clause([blobListAction()], [])
    clause([blobReadAction()], [allOf([isNotSystemContainer(), blobPathIsUnder('public')])])
  ],
  ' AND '
)
// Repository roles are registry-wide unless every granted data action is conditioned.
// Catalog listing is a separate, unconditionable role and is not needed to pull a known image.
@export()
func containerRepositoryCondition(repositories string[], writable bool) string => clause(
  map(
    concat(['content/read', 'metadata/read'], writable ? ['content/write', 'metadata/write'] : []),
    action => 'ActionMatches{\'Microsoft.ContainerRegistry/registries/repositories/${action}\'}'
  ),
  map(repositories, repository => '@Request[Microsoft.ContainerRegistry/registries/repositories:name] StringEqualsIgnoreCase \'${repository}\'')
)
// The CI publishing identity (puck official upload) writes only to the platform's own container
// (named by the Front Door identity's principal id), under one fixed prefix. Unlike
// byteTerraceApiHostStorageUserCondition (any non-private path) this grants no delete-only
// carve-out and no cross-prefix read: official content has one writer, so there is no
// migration-drain or Frozen-metadata story to accommodate.
@export()
func officialContentPublisherCondition() string => join(
  [
    clause(
      [
        blobAction('add/action')
        blobAction('delete')
        blobAction('move/action')
        blobReadAction()
        blobAction('write')
      ],
      [allOf([isNotSystemContainer(), blobPathIsUnder('public/puck/official')])]
    )
    clause([blobListAction()], [listPrefixIsUnderAnyOf(['public/puck/official'])])
  ],
  ' AND '
)
