@{
    Severity = @('Error','Warning')
    IncludeRules = @(
        'PSAvoidAssignmentToAutomaticVariable'
        'PSAvoidUsingInvokeExpression'
        'PSAvoidUsingPlainTextForPassword'
        'PSAvoidUsingConvertToSecureStringWithPlainText'
        'PSUseDeclaredVarsMoreThanAssignments'
        'PSAvoidUsingWMICmdlet'
    )
}
